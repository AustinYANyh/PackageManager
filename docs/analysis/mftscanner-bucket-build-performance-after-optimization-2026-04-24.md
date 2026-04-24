# MftScanner 建桶优化后性能分析报告

## 1. 文档信息

- 文档名称：MftScanner 建桶优化后性能分析报告
- 分析日期：2026-04-24
- 分析目标：确认本次 `ContainsAccelerator` 重构与并行建桶优化是否已实际生效，并评估优化幅度
- 分析方式：基于运行目录二进制时间戳 + 真实索引性能日志交叉判断

## 2. 结论摘要

本次建桶优化已经实际运行生效，且效果明显。

可以明确确认的结论：

- `23:26` 这一轮启动样本已经吃到优化后的 `MftScanner.Core.dll`
- `trigram-ready` 从早期样本的 `5975 ms` 降到了 `1746 ms`
- `full warmup` 从早期样本的 `17054 ms` 降到了 `6387 ms`
- 建桶总耗时下降明显，已经不是之前那种 `14~17 秒` 级别
- 优化后新的主要瓶颈开始转向 `SNAPSHOT LOAD`，而不是桶构建本身

## 3. 证据链

### 3.1 运行二进制时间

运行目录中的核心 DLL 时间如下：

- `D:\工作区域\PackageManager\Assets\Tools\MftScanner.Core.dll`
- `LastWriteTime = 2026/4/24 23:25:28`

开发输出目录中的 DLL 时间一致：

- `D:\工作区域\PackageManager\Tools\MftScanner.Core\bin\Debug\MftScanner.Core.dll`
- `LastWriteTime = 2026/4/24 23:25:28`

### 3.2 日志时间

索引性能日志：

- `C:\Users\AustinYan\AppData\Local\PackageManager\logs\index-service-diagnostics\20260424.log`
- `LastWriteTime = 2026/4/24 23:27:57`

这说明：

- `23:25:28` 之后产出的日志样本，有条件被视为“优化后代码正在运行”的有效样本
- `23:26:06` 开始的一整轮 `SNAPSHOT LOAD / RESTORE / CATCHUP / CONTAINS WARMUP` 可以作为本次优化后的主样本

## 4. 优化前后对比

### 4.1 早期基线样本

基线样本来自同一天较早时段：

```text
2026-04-24 21:58:07.782 [INDEX] [CONTAINS WARMUP] outcome=stage reason=post-catchup stage=trigram-ready elapsedMs=5975
2026-04-24 21:58:18.860 [INDEX] [CONTAINS WARMUP] outcome=success reason=post-catchup elapsedMs=17054
```

对应结论：

- `trigram-ready = 5975 ms`
- `full warmup = 17054 ms`

### 4.2 优化后样本

有效样本来自 `23:26` 之后：

```text
2026-04-24 23:26:06.889 [INDEX] [CONTAINS WARMUP] outcome=start reason=post-catchup records=1126788
2026-04-24 23:26:08.635 [INDEX] [CONTAINS WARMUP] outcome=stage reason=post-catchup stage=trigram-ready elapsedMs=1746
2026-04-24 23:26:13.277 [INDEX] [CONTAINS WARMUP] outcome=success reason=post-catchup elapsedMs=6387
```

对应结论：

- `trigram-ready = 1746 ms`
- `full warmup = 6387 ms`

### 4.3 优化幅度

按日志直接计算：

- `trigram-ready`：`5975 -> 1746 ms`
  - 下降约 `70.8%`
- `full warmup`：`17054 -> 6387 ms`
  - 下降约 `62.5%`

这说明本次优化不是轻微波动，而是明确的数量级下降。

## 5. 启动链路分析

优化后 `23:26` 这一轮完整启动样本如下：

```text
2026-04-24 23:26:05.347 [INDEX] [SNAPSHOT LOAD] hit elapsedMs=2870
2026-04-24 23:26:06.012 [INDEX] [SNAPSHOT RESTORE] elapsedMs=663
2026-04-24 23:26:06.013 [INDEX] [SNAPSHOT RESTORE TOTAL] totalMs=3535 loadMs=2870 restoreMs=663
2026-04-24 23:26:06.888 [INDEX] [SNAPSHOT CATCHUP TOTAL] catchUpMs=777 applyMs=768 totalChanges=1071
2026-04-24 23:26:08.635 [INDEX] [CONTAINS WARMUP] outcome=stage ... elapsedMs=1746
2026-04-24 23:26:13.277 [INDEX] [CONTAINS WARMUP] outcome=success ... elapsedMs=6387
```

可以得到：

- 快照加载：`2870 ms`
- 快照恢复：`663 ms`
- 恢复总耗时：`3535 ms`
- USN 追平总耗时：`777 ms`
- trigram-ready：`1746 ms`
- full warmup：`6387 ms`

### 5.1 当前瓶颈排序

在优化后的这轮样本里，瓶颈排序已经发生变化：

1. `SNAPSHOT LOAD`
2. `CONTAINS WARMUP`
3. `SNAPSHOT CATCHUP APPLY`

这说明当前系统的性能重点，已经不再是“桶构建完全压垮启动链路”，而是：

- 建桶仍然重，但已可接受很多
- 快照加载开始成为新的第一大头

## 6. 查询性能分析

优化后 warmup 完成后的查询样本如下。

### 6.1 bigram / trigram 查询

```text
2026-04-24 23:27:01.169 [INDEX] [SEARCH] ... keyword=wx totalMs=4 matchMs=1
2026-04-24 23:27:03.358 [INDEX] [SEARCH] ... keyword=ddx totalMs=4 matchMs=4
2026-04-24 23:27:04.625 [INDEX] [SEARCH] ... keyword=dx totalMs=1 matchMs=0
2026-04-24 23:27:08.540 [INDEX] [SEARCH] ... keyword=wda totalMs=0 matchMs=0
```

结论：

- `bigram / trigram` 查询整体非常稳定
- 热态下大多保持在 `0~4 ms`
- 当前 contains 索引层已经很健康

### 6.2 单字符查询

```text
2026-04-24 23:27:04.215 [INDEX] [SEARCH] ... keyword=d totalMs=25 matchMs=24 matched=660139
2026-04-24 23:27:07.632 [INDEX] [SEARCH] ... keyword=w totalMs=6 matchMs=5 matched=249967
2026-04-24 23:27:12.586 [INDEX] [SEARCH] ... keyword=e totalMs=29 matchMs=28 matched=826211
2026-04-24 23:27:14.667 [INDEX] [SEARCH] ... keyword=e totalMs=57 matchMs=56 matched=826211
```

结论：

- 高频单字符仍是最差路径
- 即便如此，热态下也基本维持在几十毫秒内
- 这说明优化后结构已经足以把高频单字符压到可接受范围

## 7. IPC 与 UI 体感分析

日志显示，索引层并不是所有“慢感”的来源。

例如：

```text
2026-04-24 23:27:14.667 [INDEX] [SEARCH] ... keyword=e totalMs=57
2026-04-24 23:27:14.701 [IPC] [MMF] ... totalMs=163
2026-04-24 23:27:14.708 [UI] [CTRLE UI SEARCH] ... totalUiSearchMs=170
```

可以看出：

- 索引层搜索：`57 ms`
- IPC：`163 ms`
- UI 总耗时：`170 ms`

这说明在当前优化后：

- 索引层已明显快于 UI/IPC 链路
- 后续如果继续追用户体感，不能只看索引算法

## 8. 对本次优化的判断

结合日志和运行二进制时间，可以得出明确结论：

- 本次 `ContainsAccelerator` 重构和并行建桶已实际运行
- 优化后的 `warmup` 时间显著下降
- 本次优化是有效的大幅优化，不是偶然波动

更具体地说：

- 原先 `full warmup` 长期处于 `14~17 秒` 级别
- 当前样本已压到 `6.387 秒`
- `trigram-ready` 已压到 `1.746 秒`

因此，本轮工作已经成功解决了“建桶过慢是绝对主瓶颈”的问题。

## 9. 当前仍存在的问题

虽然建桶已明显改善，但还未达到“完全无感”：

- `full warmup` 依然有约 `6.4 秒`
- 宿主冷启动时，仍存在一段“已恢复但未 fully warm”的窗口
- 当前新的最大瓶颈已经转向 `SNAPSHOT LOAD`

也就是说，下一阶段若继续做性能专项，最值得优先看的会是：

1. 快照加载速度
2. 快照文件结构与反序列化成本
3. UI / IPC 体感链路

## 10. 最终结论

本次日志足以确认：

- 现在看到的 `23:26` 之后样本是优化后建桶实现的真实运行结果
- 建桶优化已生效
- `trigram-ready` 和 `full warmup` 均有大幅下降
- 当前瓶颈已从“建桶本身”开始转移到“快照加载 + UI/IP C链路”

这意味着本轮优化是成功的，而且已经把问题推进到了下一阶段。
