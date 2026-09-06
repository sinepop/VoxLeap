#!/usr/bin/env python3
"""ASR Provider 评测器（P0-06 工具链）。

输入：

- 参考语料：tests/fixtures/asr-corpus.json（100 条合成文本，仅含 id/category/text）；
- 评测记录 JSON：每个 Provider 一份，格式见 tests/asr-eval/README.md。

输出：

- 汇总 JSON（延迟分位数、CER/WER、技术词与改口通过率、失败与警告清单）；
- Markdown 报告（与 docs/06 的 Provider 对比表同构，结论字段留待人工填写）。

原则：

- 失败与超时单独统计，绝不当作空字符串参与准确率计算；
- repair_relation_ok 属于人工判断项：repair 类样本缺失该字段时计为不通过并报警，
  不自动补真、也不默认通过；
- 默认不在输出中保留识别正文（hypothesis），需要复核时显式传 --include-text。
"""

from __future__ import annotations

import argparse
import json
import math
import sys
from pathlib import Path

from scoring import cer, median, percentile, punctuation_ok, technical_token_check, wer

REPAIR_CATEGORY = "repair"
DEFAULT_CORPUS = Path(__file__).resolve().parent.parent / "fixtures" / "asr-corpus.json"


def load_json(path: Path):
    # utf-8-sig 兼容带 BOM 的文件
    with path.open(encoding="utf-8-sig") as fh:
        return json.load(fh)


def _num_or_none(value):
    """只接受非负有限数值；布尔与 NaN/Infinity 一律视为缺失（由警告提示）。"""
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        return None
    if not math.isfinite(value) or value < 0:
        return None
    return float(value)


def _is_failed_flag(raw) -> tuple[bool, bool]:
    """解析 request_failed。返回 (是否失败, 类型是否规范)。"""
    if isinstance(raw, bool):
        return raw, True
    if isinstance(raw, str) and raw.casefold() in ("true", "false"):
        return raw.casefold() == "true", False
    return bool(raw), False


def evaluate_results(corpus: list[dict], results: dict, include_text: bool = False) -> dict:
    """对单个 Provider 的评测记录计算全部指标，返回汇总字典。"""
    corpus_by_id = {entry["id"]: entry for entry in corpus}
    warnings: list[dict] = []
    failures: list[dict] = []
    missing_ids: list[str] = []

    scored: list[dict] = []
    for raw in results.get("records", []):
        sid = raw.get("sample_id")
        entry = corpus_by_id.get(sid)
        if entry is None:
            warnings.append({"type": "unknown_sample", "sample_id": sid})
            continue
        record = {
            "sample_id": sid,
            "category": entry.get("category", "unknown"),
            "condition": raw.get("condition"),
        }
        if raw.get("data_retention_note") is not None:
            record["data_retention_note"] = raw["data_retention_note"]
        if include_text:
            record["reference"] = entry["text"]
            record["hypothesis"] = raw.get("hypothesis")

        failed, flag_type_ok = _is_failed_flag(raw.get("request_failed", False))
        if not flag_type_ok:
            warnings.append(
                {
                    "type": "invalid_request_failed_type",
                    "sample_id": sid,
                    "note": f"request_failed 应为布尔值，实际为 {raw.get('request_failed')!r}，已按 {failed} 处理",
                }
            )
        if failed:
            reason = raw.get("failure_reason") or "unspecified"
            failures.append({"sample_id": sid, "reason": reason})
            record["status"] = "failed"
            record["failure_reason"] = reason
            scored.append(record)
            continue

        hyp = raw.get("hypothesis")
        if hyp is None or not str(hyp).strip():
            failures.append({"sample_id": sid, "reason": "empty_hypothesis"})
            record["status"] = "failed"
            record["failure_reason"] = "empty_hypothesis"
            warnings.append(
                {
                    "type": "empty_hypothesis_not_marked_failed",
                    "sample_id": sid,
                    "note": "request_failed=false 但识别结果为空，已按失败统计",
                }
            )
            scored.append(record)
            continue

        record["status"] = "ok"
        record["hypothesis_empty"] = False
        record["first_partial_ms"] = _num_or_none(raw.get("first_partial_ms"))
        record["final_result_ms"] = _num_or_none(raw.get("final_result_ms"))
        if record["first_partial_ms"] is None or record["final_result_ms"] is None:
            warnings.append({"type": "missing_timing", "sample_id": sid})
        record["cer"] = cer(entry["text"], str(hyp))
        record["wer"] = wer(entry["text"], str(hyp))
        record["punctuation_ok"] = punctuation_ok(entry["text"], str(hyp))

        tech = technical_token_check(entry["text"], str(hyp))
        record["technical_applicable"] = tech is not None
        if tech is not None:
            record["technical_ok"] = tech["ok"]
            record["technical_missing"] = tech["missing"]

        record["repair_required"] = record["category"] == REPAIR_CATEGORY
        if record["repair_required"]:
            if raw.get("repair_relation_ok") is None:
                record["repair_ok"] = False
                warnings.append(
                    {
                        "type": "missing_repair_relation",
                        "sample_id": sid,
                        "note": "repair 类样本必须人工给出 repair_relation_ok，缺失按不通过计",
                    }
                )
            else:
                record["repair_ok"] = bool(raw.get("repair_relation_ok"))
        scored.append(record)

    recorded_ids = {r.get("sample_id") for r in results.get("records", [])}
    missing_ids = sorted(set(corpus_by_id) - recorded_ids)
    if missing_ids:
        warnings.append(
            {"type": "missing_samples", "count": len(missing_ids), "sample_ids": missing_ids}
        )

    return build_summary(results, scored, failures, warnings)


def build_summary(results: dict, scored: list[dict], failures: list[dict], warnings: list[dict]) -> dict:
    ok_records = [r for r in scored if r["status"] == "ok"]

    first_ms = [r["first_partial_ms"] for r in ok_records if r["first_partial_ms"] is not None]
    final_ms = [r["final_result_ms"] for r in ok_records if r["final_result_ms"] is not None]
    cers = [r["cer"] for r in ok_records if r["cer"] is not None]
    wers = [r["wer"] for r in ok_records if r["wer"] is not None]

    timing = {
        "first_partial_ms": {"median": median(first_ms), "p95": percentile(first_ms, 95), "n": len(first_ms)},
        "final_result_ms": {"median": median(final_ms), "p95": percentile(final_ms, 95), "n": len(final_ms)},
    }

    categories: dict[str, dict] = {}
    for r in ok_records:
        cat = categories.setdefault(
            r["category"],
            {
                "n": 0,
                "cer_values": [],
                "wer_values": [],
                "technical_applicable": 0,
                "technical_passed": 0,
                "repair_required": 0,
                "repair_passed": 0,
                "punctuation_passed": 0,
            },
        )
        cat["n"] += 1
        if r["cer"] is not None:
            cat["cer_values"].append(r["cer"])
        if r["wer"] is not None:
            cat["wer_values"].append(r["wer"])
        if r.get("technical_applicable"):
            cat["technical_applicable"] += 1
            if r.get("technical_ok"):
                cat["technical_passed"] += 1
        if r.get("repair_required"):
            cat["repair_required"] += 1
            if r.get("repair_ok"):
                cat["repair_passed"] += 1
        if r.get("punctuation_ok"):
            cat["punctuation_passed"] += 1

    category_out = {}
    for name, c in sorted(categories.items()):
        category_out[name] = {
            "n": c["n"],
            "cer_median": median(c["cer_values"]),
            "wer_median": median(c["wer_values"]),
            "technical": {
                "applicable": c["technical_applicable"],
                "passed": c["technical_passed"],
                "rate": round(c["technical_passed"] / c["technical_applicable"], 4)
                if c["technical_applicable"]
                else None,
            },
            "repair_relation": {
                "required": c["repair_required"],
                "passed": c["repair_passed"],
                "rate": round(c["repair_passed"] / c["repair_required"], 4)
                if c["repair_required"]
                else None,
            },
            "punctuation_passed": c["punctuation_passed"],
        }

    return {
        "provider": results.get("provider", "unknown"),
        "meta": results.get("meta", {}),
        "totals": {
            "corpus_size": None,  # 由调用方回填
            "recorded": len(scored),
            "successes": len(ok_records),
            "failures": len(failures),
        },
        "timing": timing,
        "accuracy": {"cer_median": median(cers), "wer_median": median(wers)},
        "categories": category_out,
        "failures": failures,
        "warnings": warnings,
        "records": scored,
    }


# ---------------------------------------------------------------------------
# Markdown 报告
# ---------------------------------------------------------------------------

def fmt_ms(v) -> str:
    return f"{v:.0f}" if v is not None else "—"


def fmt_rate(v) -> str:
    return f"{v * 100:.1f}%" if v is not None else "—"


def fmt_pct(v) -> str:
    return f"{v * 100:.2f}%" if v is not None else "—"


def render_markdown(summary: dict) -> str:
    meta = summary.get("meta", {})
    timing = summary["timing"]
    acc = summary["accuracy"]
    tech_all = {"applicable": 0, "passed": 0}
    for c in summary["categories"].values():
        tech_all["applicable"] += c["technical"]["applicable"]
        tech_all["passed"] += c["technical"]["passed"]
    tech_rate = (
        tech_all["passed"] / tech_all["applicable"] if tech_all["applicable"] else None
    )

    lines: list[str] = []
    lines.append("# ASR Provider 评测报告")
    lines.append("")
    lines.append(f"- Provider：`{summary['provider']}`")
    if meta:
        for key in ("protocol", "provider_version", "device", "network", "sample_rate", "recorded_at"):
            if key in meta:
                lines.append(f"- {key}：{meta[key]}")
    if meta.get("data_retention_note"):
        lines.append(f"- 保留政策：{meta['data_retention_note']}")
    lines.append(
        f"- 样本：成功 {summary['totals']['successes']} / 失败 {summary['totals']['failures']}"
        f"（语料 {summary['totals']['corpus_size']} 条）"
    )
    lines.append("")
    lines.append("## 与 docs/06 对比表同构的结果行")
    lines.append("")
    lines.append(
        "| Provider | 协议 | 首字 P95(ms) | 最终 P95(ms) | CER 中位 | 技术词通过率 | 结论 |"
    )
    lines.append("|---|---|---:|---:|---:|---:|---|")
    lines.append(
        f"| {summary['provider']} | {meta.get('protocol', '—')} | {fmt_ms(timing['first_partial_ms']['p95'])} "
        f"| {fmt_ms(timing['final_result_ms']['p95'])} | {fmt_pct(acc['cer_median'])} "
        f"| {fmt_rate(tech_rate)} | 待人工填写 |"
    )
    lines.append("")
    lines.append("## 分类明细")
    lines.append("")
    lines.append(
        "| 类别 | n | CER 中位 | WER 中位 | 技术词通过 | 改口通过 | 标点通过 |"
    )
    lines.append("|---|---:|---:|---:|---:|---:|---:|")
    for name, c in summary["categories"].items():
        lines.append(
            f"| {name} | {c['n']} | {fmt_pct(c['cer_median'])} | {fmt_pct(c['wer_median'])} "
            f"| {fmt_rate(c['technical']['rate'])} | {fmt_rate(c['repair_relation']['rate'])} "
            f"| {fmt_rate(c['punctuation_passed'] / c['n']) if c['n'] else '—'} |"
        )
    lines.append("")
    lines.append("## 延迟")
    lines.append("")
    lines.append("| 指标 | 中位(ms) | P95(ms) | n |")
    lines.append("|---|---:|---:|---:|")
    for label, key in (("首个临时结果 first_partial_ms", "first_partial_ms"), ("最终结果 final_result_ms", "final_result_ms")):
        t = timing[key]
        lines.append(f"| {label} | {fmt_ms(t['median'])} | {fmt_ms(t['p95'])} | {t['n']} |")
    lines.append("")
    if summary["failures"]:
        lines.append("## 失败样本")
        lines.append("")
        lines.append("| sample_id | 失败原因 |")
        lines.append("|---|---|")
        for f in summary["failures"]:
            lines.append(f"| {f['sample_id']} | {f['reason']} |")
        lines.append("")
    if summary["warnings"]:
        lines.append("## 警告")
        lines.append("")
        for w in summary["warnings"]:
            desc = w.get("note", json.dumps(w, ensure_ascii=False))
            sid = f"（{w['sample_id']}）" if w.get("sample_id") else ""
            lines.append(f"- `{w['type']}`{sid}：{desc}")
        lines.append("")
    lines.append("> 结论字段必须由人工在核对原始记录后填写；本报告由评测器生成，不代表任何 Provider 已通过验收。")
    lines.append("")
    return "\n".join(lines)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="ASR Provider 评测器（P0-06）")
    parser.add_argument("results", type=Path, help="单个 Provider 的评测记录 JSON")
    parser.add_argument("--corpus", type=Path, default=DEFAULT_CORPUS, help="参考语料 JSON")
    parser.add_argument("--out-json", type=Path, default=None, help="汇总 JSON 输出路径")
    parser.add_argument("--out-md", type=Path, default=None, help="Markdown 报告输出路径")
    parser.add_argument(
        "--include-text",
        action="store_true",
        help="在输出中保留参考文本与识别正文（仅本地复核用，默认关闭）",
    )
    args = parser.parse_args(argv)

    corpus = load_json(args.corpus)
    results = load_json(args.results)
    summary = evaluate_results(corpus, results, include_text=args.include_text)
    summary["totals"]["corpus_size"] = len(corpus)

    brief = {
        "provider": summary["provider"],
        "totals": summary["totals"],
        "timing": summary["timing"],
        "accuracy": summary["accuracy"],
        "failures": len(summary["failures"]),
        "warnings": len(summary["warnings"]),
    }
    print(json.dumps(brief, ensure_ascii=False, indent=2))

    if args.out_json:
        args.out_json.parent.mkdir(parents=True, exist_ok=True)
        args.out_json.write_text(
            json.dumps(summary, ensure_ascii=False, indent=2), encoding="utf-8"
        )
        print(f"汇总 JSON 已写入：{args.out_json}", file=sys.stderr)
    if args.out_md:
        args.out_md.parent.mkdir(parents=True, exist_ok=True)
        args.out_md.write_text(render_markdown(summary), encoding="utf-8")
        print(f"Markdown 报告已写入：{args.out_md}", file=sys.stderr)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
