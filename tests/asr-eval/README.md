# ASR 评测工具链（P0-06）

运行版现在会在本地完成 VAD 裁剪并通过可替换的 `IStreamingAsrProvider` 发送识别请求。评测工具仍然只消费人工导出的结果，不会自动上传录音或伪造真实数据。

把 [`docs/06-Phase0-ASR基准测试.md`](../../docs/06-Phase0-ASR基准测试.md) 定义的评分口径实现为可运行的本地工具。**当前只完成了工具链和自测，尚未对任何真实 Provider 或真实录音运行过；所有结论仍为空。**

## 组成

| 文件 | 作用 |
|---|---|
| `scoring.py` | 评分库：CER/WER、技术 Token 提取与比对、标点检查、分位数统计 |
| `evaluate.py` | 评测 CLI：读入单 Provider 评测记录，输出汇总 JSON 和 Markdown 报告 |
| `test_scoring.py` | 单元测试 + 端到端自测（34 项，全部为手工构造的模拟数据并显式标注） |

## 自测与对抗不变量

```bash
cd tests/asr-eval
python3 -m unittest test_scoring -v
```

除 44 项常规单测外，对抗式审查（2026-09-03）固化了三条不变量回归，任何实现改动不得打破：

1. **ref == hyp 必须全过**：100 条语料逐条验证 CER/WER/标点/Token 检查（曾抓到词边界检查建在去空格底板上导致 24 条误报的缺陷）；
2. **CER 等价变体不得误报**：去空格、词周加空格、全角→半角标点三种变换下 tokens/mixed_tech 全部通过（曾抓到 URL/版本子串匹配在空格插入后失效的缺陷）；
3. **删除 Token 必须报警**：逐个删除全部出现位置，32/32 捕获。

## 已知取舍与局限

- 检查偏向低误报，漏检由 CER 兑底；
- 标识符大小写敏感后，ASR 大小写漂移会记为未保留（符合产品约束 3，属有意行为）；
- CER 归一化保留 ASCII `.`（版本号完整性优先），中文句号与半角句号在 CER 上不对称，属已知口径；
- 同一 Token 多次出现只按存在性判定；
- 重复 `sample_id`（docs/06 要求每条 ≥3 次重复）会逐条计入统计，无需去重。

## 评分口径（与 docs/06 对齐）

- `cer`：移除空白与标点、保留字母/数字/CJK/连接符后按字符计算 Levenshtein / 参考长度；
- `wer`：移除标点、casefold 后按空白切词计算。纯中文无空格文本上 WER 退化为 0 或整句错，主要对 `mixed_tech` / `tokens` 有意义；
- `technical_token_ok`：只检查 docs/06 列举的高风险 Token，按 URL → 版本号 → 连接符标识符 → 点分路径 → 数字 的优先级提取，逐项检查保留情况；无可提取 Token 时记为“不适用”，不算通过也不算失败；
  - 标识符/点分路径**大小写敏感**（代码标识符的大小写即语义），允许连接符被空格替代；
  - URL 匹配在去空白底板上进行（口述 URL 本无空格），域名按规范大小写不敏感；
  - 数字边界只看相邻数字：ref “react 18” 与 hyp “react18” 互为 CER 等价，双向都判保留；“18” 在 “180” 中不算保留；
  - 普通英文词不逐词强制（词边界在 CER 等价变换下不可靠），由 CER 兑底；
  - 存在性语义：Token 出现 ≥1 次即算保留，不校验出现次数；
- `punctuation_ok`：参考句以句末标点收尾时，识别结果也须以句末标点收尾；半角/全角句末标点互相认可；
- 失败与超时单独统计，绝不当作空字符串参与准确率；
- `repair_relation_ok` 是人工判断项：`repair` 类样本缺失该字段时按不通过计并输出警告，工具不会自动补真。

## 评测记录输入格式

每个 Provider 一份 JSON：

```json
{
  "provider": "示例供应商ID",
  "meta": {
    "protocol": "websocket",
    "provider_version": "2026-09-03",
    "device": "测试设备与麦克风",
    "network": "网络条件",
    "sample_rate": 16000,
    "recorded_at": "2026-09-03",
    "data_retention_note": "供应商数据保留政策说明"
  },
  "records": [
    {
      "sample_id": "tokens-001",
      "condition": "quiet",
      "first_partial_ms": 480,
      "final_result_ms": 1250,
      "hypothesis": "识别出的文本（仅用于本地评分，默认不进报告）",
      "request_failed": false,
      "failure_reason": null,
      "repair_relation_ok": null,
      "data_retention_note": null
    }
  ]
}
```

字段规则：

- `request_failed=true` 时必须给 `failure_reason`，缺失记为 `unspecified`；
- `request_failed=false` 但 `hypothesis` 为空，按 `empty_hypothesis` 失败处理并输出警告，防止“空结果混进准确率”；
- `repair` 类样本必须人工填写 `repair_relation_ok`（true/false），缺失按不通过计；
- 未知 `sample_id`、缺失样本、缺失时延都会进入 `warnings`，不会被静默吞掉。

## 运行评测（待真实数据）

```bash
cd tests/asr-eval
python3 evaluate.py <provider-results.json> \
  --out-json summary-<provider>.json \
  --out-md report-<provider>.md
```

默认输出不含识别正文；需要本地复核时加 `--include-text`，复核产物不得提交进仓库。

## 边界声明

- 本工具不采集音频、不调用任何网络服务；
- 报告中的“结论”列永远留空，必须人工核对原始记录后填写；
- 在真实录音 + 真实 Provider 数据产生之前，任何通过率都不得写入 PROGRESS.md 或对外引用。
