"""ASR 评测评分库（P0-06 工具链）。

评分口径与 docs/06-Phase0-ASR基准测试.md 对齐：

- cer：中文字符错误率。归一化时移除空白与标点，保留字母/数字/CJK，拉丁字母 casefold，
  然后按字符级 Levenshtein 距离 / 参考长度 计算。
- wer：按空格切分的词错误率。归一化时移除标点、casefold 后按空白切词。
  纯中文无空格文本上 WER 会退化为 0 或整句错，主要对 mixed_tech / tokens 有意义。
- technical_token_ok：从参考文本中确定性提取 URL、版本号、代码标识符、点分路径和数字，
  逐项检查是否保留。没有可提取 Token 时返回 None（不适用），不得当成通过或失败。
  普通英文词不逐词强制（docs/06 未要求，且词边界在 CER 等价变换下不可靠），
  由 CER 兜底；检查偏向低误报，漏检由 CER 兑底。
- punctuation_ok：参考句以句末标点收尾时，识别结果也应以句末标点收尾，且结果非空。

本库只做确定性计算，不做任何“语义正确”的推断；改口/否定关系（repair_relation_ok）
必须由人工在评测记录中显式给出，缺失会被评测器报警而不是默认通过。
"""

from __future__ import annotations

import re
import unicodedata
from dataclasses import dataclass, field

# ---------------------------------------------------------------------------
# 归一化
# ---------------------------------------------------------------------------

def _is_kept_for_cer(ch: str) -> bool:
    """CER 归一化保留：字母、数字、CJK 等实义字符；丢弃空白与标点符号。"""
    if ch.isspace():
        return False
    cat = unicodedata.category(ch)
    if cat.startswith(("P", "Z", "S")) and ch not in ("_", "-", "."):
        return False
    return cat.startswith(("L", "N")) or ch in ("_", "-", ".")


def normalize_for_cer(text: str) -> str:
    """移除空白与标点，保留字母/数字/连接符/CJK，拉丁字母 casefold。"""
    return "".join(
        ch.casefold() if ch.isascii() else ch for ch in text if _is_kept_for_cer(ch)
    ).strip()


def _is_punct(ch: str) -> bool:
    cat = unicodedata.category(ch)
    return cat.startswith("P") or cat.startswith("S")


def normalize_for_wer(text: str) -> list[str]:
    """移除标点、casefold 后按空白切词；返回词列表。"""
    cleaned = "".join(" " if _is_punct(ch) else ch.casefold() if ch.isascii() else ch for ch in text)
    return cleaned.split()


# ---------------------------------------------------------------------------
# 编辑距离
# ---------------------------------------------------------------------------

def levenshtein(a: str, b: str) -> int:
    """字符级 Levenshtein 距离（两行滚动数组实现）。"""
    if a == b:
        return 0
    if not a:
        return len(b)
    if not b:
        return len(a)
    prev = list(range(len(b) + 1))
    for i, ca in enumerate(a, start=1):
        cur = [i]
        for j, cb in enumerate(b, start=1):
            cost = 0 if ca == cb else 1
            cur.append(min(prev[j] + 1, cur[j - 1] + 1, prev[j - 1] + cost))
        prev = cur
    return prev[-1]


def cer(reference: str, hypothesis: str) -> float | None:
    """字符错误率；参考文本归一化后为空时返回 None（无法定义）。"""
    ref_n = normalize_for_cer(reference)
    if not ref_n:
        return None
    hyp_n = normalize_for_cer(hypothesis)
    return levenshtein(ref_n, hyp_n) / len(ref_n)


def wer(reference: str, hypothesis: str) -> float | None:
    """词错误率；参考词序列为空时返回 None。"""
    ref_w = normalize_for_wer(reference)
    if not ref_w:
        return None
    hyp_w = normalize_for_wer(hypothesis)
    return levenshtein(ref_w, hyp_w) / len(ref_w)


# ---------------------------------------------------------------------------
# 技术 Token 提取与比对
# ---------------------------------------------------------------------------

_URL_RE = re.compile(r"(?:https?://|www\.)[^\s，。；、！？\"'）]+", re.IGNORECASE)
_VERSION_RE = re.compile(r"(?<![\d.])\d+(?:\.\d+)+\b")
_IDENT_RE = re.compile(r"[A-Za-z][A-Za-z0-9]*(?:[_\-][A-Za-z0-9]+)+")
_DOTTED_RE = re.compile(r"\b[a-zA-Z][A-Za-z0-9]*(?:\.[A-Za-z0-9]+)+")
_NUMBER_RE = re.compile(r"\d+")


def _mask(text: str, spans: list[tuple[int, int]]) -> str:
    return "".join("\x00" if any(s <= i < e for s, e in spans) else ch for i, ch in enumerate(text))


def _dedup(items: list[str]) -> list[str]:
    seen: set[str] = set()
    out = []
    for it in items:
        key = it.casefold()
        if key not in seen:
            seen.add(key)
            out.append(it)
    return out


@dataclass
class ExpectedTokens:
    urls: list[str] = field(default_factory=list)
    versions: list[str] = field(default_factory=list)
    identifiers: list[str] = field(default_factory=list)
    dotted: list[str] = field(default_factory=list)
    numbers: list[str] = field(default_factory=list)

    def total(self) -> int:
        return sum(len(v) for v in (self.urls, self.versions, self.identifiers, self.dotted, self.numbers))


def extract_tokens(text: str) -> ExpectedTokens:
    """按 URL → 版本号 → 标识符 → 点分路径 → 数字 的优先级提取，先命中者占位。"""
    out = ExpectedTokens()
    work = text
    out.urls = _dedup(_URL_RE.findall(work))
    work = _mask(work, [m.span() for m in _URL_RE.finditer(work)])
    out.versions = _dedup(_VERSION_RE.findall(work))
    work = _mask(work, [m.span() for m in _VERSION_RE.finditer(work)])
    out.identifiers = _dedup(_IDENT_RE.findall(work))
    work = _mask(work, [m.span() for m in _IDENT_RE.finditer(work)])
    out.dotted = _dedup(_DOTTED_RE.findall(work))
    work = _mask(work, [m.span() for m in _DOTTED_RE.finditer(work)])
    out.numbers = _dedup(_NUMBER_RE.findall(work))
    return out


def _boundary_base(text: str) -> str:
    """数字边界匹配底板：非 [a-z0-9] 字符（含 CJK/标点/空白/连接符）一律视为边界。"""
    return re.sub(r"[^a-z0-9]+", " ", text.casefold()).strip()


def _ident_base(text: str) -> str:
    """大小写保留、仅折叠空白的底板，用于代码标识符的大小写敏感匹配。"""
    return re.sub(r"\s+", " ", text)


def _digit_boundary_hit(number: str, base: str) -> bool:
    """数字只在相邻是数字时才视为被吞并；与字母相邻不算丢失。

    只用数字做边界，是因为 ref/hyp 的空格差异不应造成非对称误报：
    ref "react 18" 与 hyp "react18" 互为 CER 等价，两种写法都必须判定为保留。
    """
    return re.search(r"(?<![0-9])" + re.escape(number) + r"(?![0-9])", base) is not None


def technical_token_check(reference: str, hypothesis: str) -> dict | None:
    """检查参考文本中的高风险 Token（URL/版本/标识符/点分路径/数字）是否保留。

    返回 None 表示参考文本没有可提取的 Token（该项不适用）；
    返回 {"ok": bool, "missing": [...], "checked": n}。

    已知取舍（偏向低误报，漏检由 CER 兑底）：
    - 标识符/点分路径大小写敏感（代码标识符的大小写即语义，对应产品约束 3），
      允许连接符被空格替代，但不允许大小写漂移；
    - URL 按域名规范大小写不敏感；
    - 数字/标识符按存在性语义判定（出现≥1次即算保留，不校验出现次数）；
    - 普通英文词不逐词检查，词级丢失由 CER 反映。
    """
    expected = extract_tokens(reference)
    if expected.total() == 0:
        return None
    hyp_nospace = re.sub(r"\s+", "", hypothesis).casefold()
    hyp_base = _boundary_base(hypothesis)
    hyp_idb = _ident_base(hypothesis)
    missing: list[str] = []

    for url in expected.urls:
        # 口述 URL 本无空格，ASR 可能在 URL 内插空格，故在去空白底板上匹配。
        if url.casefold() not in hyp_nospace:
            missing.append(url)
    for ver in expected.versions:
        if ver.casefold() not in hyp_nospace:
            missing.append(ver)
    for ident in expected.identifiers + expected.dotted:
        # 代码标识符大小写敏感；允许连接符被空格替代（口述时常见的停顿）。
        if ident not in hyp_idb and re.sub(r"[-_.]", " ", ident) not in hyp_idb:
            missing.append(ident)
    for num in expected.numbers:
        if not _digit_boundary_hit(num, hyp_base):
            missing.append(num)

    return {
        "ok": not missing,
        "missing": missing,
        "checked": expected.total(),
    }


# ---------------------------------------------------------------------------
# 标点检查
# ---------------------------------------------------------------------------

_TERMINAL_PUNCT = "。！？!?…."


def punctuation_ok(reference: str, hypothesis: str) -> bool:
    """参考句以句末标点收尾时，识别结果也应以句末标点收尾且非空。

    半角与全角句末标点互相认可（ASR 常见输出差异，不作为丢失）。
    """
    if not hypothesis.strip():
        return False
    if reference.rstrip().endswith(tuple(_TERMINAL_PUNCT)):
        return hypothesis.rstrip().endswith(tuple(_TERMINAL_PUNCT))
    return True


# ---------------------------------------------------------------------------
# 统计
# ---------------------------------------------------------------------------

def percentile(values: list[float], p: float) -> float | None:
    """线性插值分位数；空列表返回 None。p 取 0~100。"""
    if not values:
        return None
    ordered = sorted(values)
    if len(ordered) == 1:
        return ordered[0]
    rank = (len(ordered) - 1) * (p / 100.0)
    lo = int(rank)
    hi = min(lo + 1, len(ordered) - 1)
    frac = rank - lo
    return ordered[lo] * (1 - frac) + ordered[hi] * frac


def median(values: list[float]) -> float | None:
    return percentile(values, 50)
