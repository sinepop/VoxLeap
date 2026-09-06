"""tests/asr-eval 自测（纯本地合成数据，模拟识别显式标注，不冒充真实 ASR 成果）。

运行：

    python3 -m unittest test_scoring -v
"""

from __future__ import annotations

import contextlib
import io
import json
import tempfile
import unittest
from pathlib import Path

import evaluate
import scoring

CORPUS_PATH = Path(__file__).resolve().parent.parent / "fixtures" / "asr-corpus.json"


class NormalizationTests(unittest.TestCase):
    def test_normalize_for_cer_strips_punct_and_space(self):
        self.assertEqual(scoring.normalize_for_cer("你好，世界！ Hello。"), "你好世界hello")

    def test_normalize_for_cer_keeps_connector_chars(self):
        self.assertEqual(scoring.normalize_for_cer("use_state, vite.config"), "use_statevite.config")

    def test_normalize_for_wer(self):
        self.assertEqual(scoring.normalize_for_wer("Hello, World!  nihao"), ["hello", "world", "nihao"])


class LevenshteinTests(unittest.TestCase):
    def test_known_distance(self):
        self.assertEqual(scoring.levenshtein("kitten", "sitting"), 3)

    def test_empty_sides(self):
        self.assertEqual(scoring.levenshtein("", "abc"), 3)
        self.assertEqual(scoring.levenshtein("abc", ""), 3)
        self.assertEqual(scoring.levenshtein("", ""), 0)


class CerWerTests(unittest.TestCase):
    def test_cer_exact(self):
        self.assertEqual(scoring.cer("你好，世界。", "你好世界"), 0.0)

    def test_cer_substitution(self):
        self.assertAlmostEqual(scoring.cer("ABC", "AXC"), 1 / 3)

    def test_cer_insertion(self):
        self.assertAlmostEqual(scoring.cer("你好", "你好呀"), 0.5)

    def test_cer_empty_reference_returns_none(self):
        self.assertIsNone(scoring.cer("。！？", "abc"))

    def test_wer_substitution(self):
        self.assertAlmostEqual(scoring.wer("hello world foo", "hello world bar"), 1 / 3)

    def test_wer_ignores_case_and_punct(self):
        self.assertEqual(scoring.wer("Hello, World!", "hello world"), 0.0)

    def test_wer_empty_reference_returns_none(self):
        self.assertIsNone(scoring.wer("。", "x"))


class TokenExtractionTests(unittest.TestCase):
    def test_extract_priorities_no_double_count(self):
        text = "把 react18 升级到 2.4.1，参考 https://github.com/facebook/react 和 use_state 的写法，检查 vite.config.ts，共 3 处修改。"
        tokens = scoring.extract_tokens(text)
        self.assertEqual(tokens.urls, ["https://github.com/facebook/react"])
        self.assertEqual(tokens.versions, ["2.4.1"])
        self.assertEqual(tokens.identifiers, ["use_state"])
        self.assertEqual(tokens.dotted, ["vite.config.ts"])
        # 普通英文词不提取（docs/06 口径），数字中会出现 react18 的 18
        self.assertEqual(tokens.numbers, ["18", "3"])
        # URL 内部组件不应重复计入
        self.assertNotIn("github", tokens.identifiers)
        self.assertNotIn("facebook", tokens.identifiers)

    def test_token_check_ok(self):
        ref = "请把版本号 1.2.3 写入 config.yaml。"
        res = scoring.technical_token_check(ref, "请把版本号 1.2.3 写入 config.yaml。")
        self.assertTrue(res["ok"])
        self.assertEqual(res["missing"], [])

    def test_token_check_missing_identifier(self):
        res = scoring.technical_token_check("配置 use_state 钩子", "快速调试")
        self.assertFalse(res["ok"])
        self.assertIn("use_state", res["missing"])

    def test_token_check_identifier_allows_space_substitution(self):
        res = scoring.technical_token_check("配置 use_state 钩子", "配置 use state 钩子")
        self.assertTrue(res["ok"])

    def test_token_check_identifier_case_sensitive(self):
        # 代码标识符的大小写即语义（产品约束 3）：casefold 混同 A-Z/a-z 是对抗审查抓到的真实缺陷
        res = scoring.technical_token_check(
            "这个正则表达式是 ^[A-Z][a-z]+$，大小写不能改。",
            "这个正则表达式是 ^[a-z]+$，大小写不能改。",
        )
        self.assertFalse(res["ok"])
        self.assertIn("A-Z", res["missing"])

        res2 = scoring.technical_token_check("配置 use_state 钩子", "配置 USE_STATE 钩子")
        self.assertFalse(res2["ok"])

    def test_token_check_ident_case_drift_invariant(self):
        # ref==hyp 不受大小写敏感影响
        ref = "这个正则表达式是 ^[A-Z][a-z]+$，大小写不能改。"
        self.assertTrue(scoring.technical_token_check(ref, ref)["ok"])

    def test_token_check_number_digit_boundary(self):
        # 18 出现在 180 里不算保留
        res = scoring.technical_token_check("重试 18 次", "重试 180 次")
        self.assertFalse(res["ok"])

    def test_token_check_number_letter_adjacency_still_kept(self):
        # ref "react 18" 与 hyp "react18" 互为 CER 等价，双向都应判定为保留
        self.assertTrue(scoring.technical_token_check("升级 react 18", "升级react18")["ok"])
        self.assertTrue(scoring.technical_token_check("升级 react18", "升级 react 18")["ok"])

    def test_token_check_not_applicable_for_pure_chinese(self):
        self.assertIsNone(scoring.technical_token_check("今天天气不错", "今天天气不错"))


class PunctuationTests(unittest.TestCase):
    def test_terminal_punct_preserved(self):
        self.assertTrue(scoring.punctuation_ok("今天天气不错。", "今天天气不错。"))

    def test_terminal_punct_missing(self):
        self.assertFalse(scoring.punctuation_ok("今天天气不错。", "今天天气不错"))

    def test_no_requirement_when_reference_unterminal(self):
        self.assertTrue(scoring.punctuation_ok("随便说说", "随便说说，"))

    def test_empty_hypothesis_fails(self):
        self.assertFalse(scoring.punctuation_ok("今天天气不错。", "  "))

    def test_halfwidth_terminal_accepted(self):
        self.assertTrue(scoring.punctuation_ok("今天天气不错。", "今天天气不错."))
        self.assertTrue(scoring.punctuation_ok("报告写完了?", "报告写完了？"))


class StatsTests(unittest.TestCase):
    def test_median_odd_even(self):
        self.assertEqual(scoring.median([3.0, 1.0, 2.0]), 2.0)
        self.assertEqual(scoring.median([4.0, 1.0, 2.0, 3.0]), 2.5)

    def test_percentile_linear(self):
        self.assertAlmostEqual(scoring.percentile([1.0, 2.0, 3.0, 4.0], 95), 3.85)
        self.assertAlmostEqual(scoring.percentile([10.0], 95), 10.0)

    def test_empty_returns_none(self):
        self.assertIsNone(scoring.median([]))
        self.assertIsNone(scoring.percentile([], 95))


CORPUS = [
    {"id": "daily-001", "category": "daily_cn", "text": "今天天气不错，适合出去走走。"},
    {"id": "daily-002", "category": "daily_cn", "text": "我先喝口水，然后继续检查这份报告。"},
    {"id": "tokens-001", "category": "tokens", "text": "把配置文件升级到 2.4.1，参考 https://example.com/docs 说明。"},
    {"id": "repair-001", "category": "repair", "text": "不要用红色，改成蓝色。"},
    {"id": "mixed-001", "category": "mixed_tech", "text": "在 Next.js 里用 useState 管理状态。"},
]


def make_records() -> list[dict]:
    # 以下 hypothesis 全部为手工构造的模拟识别文本，显式用于自测，不冒充真实 ASR。
    return [
        {
            "sample_id": "daily-001",
            "hypothesis": "今天天气不错，适合出去走走。",
            "first_partial_ms": 400,
            "final_result_ms": 1000,
        },
        {
            "sample_id": "daily-002",
            "hypothesis": "   ",
            "first_partial_ms": 300,
            "final_result_ms": 900,
        },
        {
            "sample_id": "tokens-001",
            "hypothesis": "把配置文件升级到 2.4.1，参考说明。",
            "first_partial_ms": 500,
            "final_result_ms": 1400,
        },
        {
            "sample_id": "repair-001",
            "hypothesis": "不要用红色，改成蓝色。",
            "first_partial_ms": 450,
            "final_result_ms": 1100,
        },
        {"sample_id": "mixed-001", "request_failed": True},
        {"sample_id": "bogus-999", "hypothesis": "不属于语料的样本"},
    ]


class EvaluateTests(unittest.TestCase):
    def setUp(self):
        self.summary = evaluate.evaluate_results(CORPUS, {"provider": "mock-asr", "records": make_records()})
        self.summary["totals"]["corpus_size"] = len(CORPUS)

    def test_counts(self):
        totals = self.summary["totals"]
        self.assertEqual(totals["corpus_size"], 5)
        self.assertEqual(totals["recorded"], 5)  # bogus-999 不计入
        self.assertEqual(totals["successes"], 3)
        self.assertEqual(totals["failures"], 2)

    def test_empty_hypothesis_treated_as_failure(self):
        reasons = {f["sample_id"]: f["reason"] for f in self.summary["failures"]}
        self.assertEqual(reasons["daily-002"], "empty_hypothesis")
        self.assertEqual(reasons["mixed-001"], "unspecified")
        types = {w["type"] for w in self.summary["warnings"]}
        self.assertIn("empty_hypothesis_not_marked_failed", types)
        self.assertIn("unknown_sample", types)

    def test_timing_stats(self):
        timing = self.summary["timing"]
        self.assertEqual(timing["first_partial_ms"]["n"], 3)
        self.assertEqual(timing["first_partial_ms"]["median"], 450.0)
        self.assertAlmostEqual(timing["first_partial_ms"]["p95"], 495.0)
        self.assertEqual(timing["final_result_ms"]["median"], 1100.0)

    def test_technical_check_counts(self):
        tokens_cat = self.summary["categories"]["tokens"]
        self.assertEqual(tokens_cat["technical"]["applicable"], 1)
        self.assertEqual(tokens_cat["technical"]["passed"], 0)  # 缺 URL
        self.assertIn("https://example.com/docs", self.summary["records"][2]["technical_missing"])

    def test_repair_requires_manual_field(self):
        repair_cat = self.summary["categories"]["repair"]["repair_relation"]
        self.assertEqual(repair_cat["required"], 1)
        self.assertEqual(repair_cat["passed"], 0)
        self.assertIn("missing_repair_relation", {w["type"] for w in self.summary["warnings"]})

    def test_repair_manual_true_counts(self):
        records = make_records()
        for r in records:
            if r["sample_id"] == "repair-001":
                r["repair_relation_ok"] = True
        summary = evaluate.evaluate_results(CORPUS, {"provider": "mock-asr", "records": records})
        self.assertEqual(summary["categories"]["repair"]["repair_relation"]["passed"], 1)
        self.assertNotIn("missing_repair_relation", {w["type"] for w in summary["warnings"]})

    def test_missing_samples_reported(self):
        summary = evaluate.evaluate_results(CORPUS, {"provider": "mock-asr", "records": [make_records()[0]]})
        types = {w["type"] for w in summary["warnings"]}
        self.assertIn("missing_samples", types)
        missing = next(w for w in summary["warnings"] if w["type"] == "missing_samples")
        self.assertEqual(missing["count"], 4)

    def test_text_not_included_by_default(self):
        record = self.summary["records"][0]
        self.assertNotIn("hypothesis", record)
        self.assertNotIn("reference", record)


class CliTests(unittest.TestCase):
    def test_main_end_to_end(self):
        with tempfile.TemporaryDirectory() as tmp:
            tmp_path = Path(tmp)
            corpus_path = tmp_path / "corpus.json"
            results_path = tmp_path / "results.json"
            corpus_path.write_text(json.dumps(CORPUS, ensure_ascii=False), encoding="utf-8")
            results_path.write_text(
                json.dumps({"provider": "mock-asr", "records": make_records()}, ensure_ascii=False),
                encoding="utf-8",
            )
            out_json = tmp_path / "summary.json"
            out_md = tmp_path / "report.md"

            stdout = io.StringIO()
            with contextlib.redirect_stdout(stdout):
                code = evaluate.main(
                    [
                        str(results_path),
                        "--corpus",
                        str(corpus_path),
                        "--out-json",
                        str(out_json),
                        "--out-md",
                        str(out_md),
                    ]
                )
            self.assertEqual(code, 0)

            brief = json.loads(stdout.getvalue())
            self.assertEqual(brief["provider"], "mock-asr")
            self.assertEqual(brief["totals"]["successes"], 3)
            self.assertEqual(brief["totals"]["failures"], 2)

            summary = json.loads(out_json.read_text(encoding="utf-8"))
            self.assertEqual(summary["totals"]["corpus_size"], 5)
            self.assertNotIn("hypothesis", summary["records"][0])

            report = out_md.read_text(encoding="utf-8")
            self.assertIn("mock-asr", report)
            self.assertIn("待人工填写", report)
            self.assertIn("empty_hypothesis", report)


class CorpusInvariantTests(unittest.TestCase):
    """对抗式审查固化的不变量：任何实现改动都不得打破。"""

    @classmethod
    def setUpClass(cls):
        with CORPUS_PATH.open(encoding="utf-8-sig") as fh:
            cls.corpus = json.load(fh)

    def test_ref_equals_hyp_passes_all_checks(self):
        """不变量 1：ref == hyp 时，CER/WER/标点/Token 检查必须全部通过。"""
        violations = []
        for e in self.corpus:
            t = e["text"]
            tok = scoring.technical_token_check(t, t)
            if scoring.cer(t, t) != 0.0:
                violations.append((e["id"], "cer"))
            if scoring.wer(t, t) != 0.0:
                violations.append((e["id"], "wer"))
            if not scoring.punctuation_ok(t, t):
                violations.append((e["id"], "punct"))
            if tok is not None and not tok["ok"]:
                violations.append((e["id"], "tech", tok["missing"]))
        self.assertEqual(violations, [])

    def test_cer_equivalent_variants_pass_tech_check(self):
        """不变量 2：仅改变空格/全半角标点的 CER 等价变体，不得误报缺 Token 或标点丢失。"""
        import re

        violations = []
        for e in self.corpus:
            if e["category"] not in ("tokens", "mixed_tech"):
                continue
            variants = {
                "nospace": e["text"].replace(" ", ""),
                "padspc": re.sub(r"([A-Za-z0-9][A-Za-z0-9._\-]*)", r" \1 ", e["text"]),
                "halfpunct": e["text"].replace("。", ".").replace("，", ","),
            }
            for vname, v in variants.items():
                tok = scoring.technical_token_check(e["text"], v)
                if tok is not None and not tok["ok"]:
                    violations.append((e["id"], vname, "tech", tok["missing"]))
                if not scoring.punctuation_ok(e["text"], v):
                    violations.append((e["id"], vname, "punct"))
        self.assertEqual(violations, [])


class RobustnessTests(unittest.TestCase):
    """对抗式审查发现的输入防御回归。"""

    CORPUS_MIN = [{"id": "daily-001", "category": "daily_cn", "text": "今天天气不错，适合出去走走。"}]

    def test_request_failed_string_false_is_success_with_warning(self):
        recs = [{"sample_id": "daily-001", "hypothesis": "今天天气不错，适合出去走走。",
                 "first_partial_ms": 400, "final_result_ms": 1000, "request_failed": "false"}]
        s = evaluate.evaluate_results(self.CORPUS_MIN, {"provider": "x", "records": recs})
        self.assertEqual((s["totals"]["successes"], s["totals"]["failures"]), (1, 0))
        self.assertIn("invalid_request_failed_type", {w["type"] for w in s["warnings"]})

    def test_request_failed_string_true_is_failure(self):
        recs = [{"sample_id": "daily-001", "request_failed": "true"}]
        s = evaluate.evaluate_results(self.CORPUS_MIN, {"provider": "x", "records": recs})
        self.assertEqual(s["totals"]["failures"], 1)
        self.assertIn("invalid_request_failed_type", {w["type"] for w in s["warnings"]})

    def test_boolean_timing_rejected(self):
        recs = [{"sample_id": "daily-001", "hypothesis": "今天天气不错，适合出去走走。",
                 "first_partial_ms": True, "final_result_ms": 1000}]
        s = evaluate.evaluate_results(self.CORPUS_MIN, {"provider": "x", "records": recs})
        self.assertEqual(s["timing"]["first_partial_ms"]["n"], 0)
        self.assertIn("missing_timing", {w["type"] for w in s["warnings"]})

    def test_infinite_timing_rejected(self):
        recs = [{"sample_id": "daily-001", "hypothesis": "今天天气不错，适合出去走走。",
                 "first_partial_ms": float("inf"), "final_result_ms": 1000}]
        s = evaluate.evaluate_results(self.CORPUS_MIN, {"provider": "x", "records": recs})
        self.assertEqual(s["timing"]["first_partial_ms"]["n"], 0)
        json.dumps(s)  # 汇总必须始终可序列化为合法 JSON


if __name__ == "__main__":
    unittest.main(verbosity=2)
