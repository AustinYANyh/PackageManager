# MftScanner 全量优化对比报告

- 报告时间：2026-04-28 17:44
- 测试机器路径前缀：`C:\Users\AustinYanyh\Desktop`
- 记录规模：约 381 万对象
- 优化前基线：`artifacts/mft-benchmark/mft-benchmark-20260428-162700.md` 及 16:26-16:28 日志
- 优化后样本：
  - InProcess 稳态：`artifacts/mft-benchmark/mft-benchmark-20260428-173838.md`
  - SharedHost v3->v4 首次启动：`artifacts/mft-benchmark/mft-benchmark-20260428-174009.md`
  - SharedHost v4 重启：`artifacts/mft-benchmark/mft-benchmark-20260428-174139.md`
  - SharedHost v4+postings 重启：`artifacts/mft-benchmark/mft-benchmark-20260428-174440.md`

## 结论

本轮优化对内存和稳态搜索有效：宿主 Private 从优化前约 `5955MB` 降到 `2100-2340MB` 区间，snapshot 写入从 `5.1-6.1s` 降到 `2.2-2.3s`，目录 FRN snapshot 从约 `381万` 项降到 `71.7万` 项。InProcess 稳态下，路径连续输入已经达到 `8ms -> 2ms -> 1ms`，全局 `d/ve` 从 `457/225ms` 降到约 `42/34ms`。

但 SharedHost 冷启动仍不合格：USN catch-up 的 `ApplyBatch` 阶段会占用约 `5.6-6.0s`，此时首个路径前置查询被锁等待，表现为 `5.3-5.8s`。这不是 contains 扫描本身慢，日志里同一查询的 contains 扫描只有 `8-16ms`。另外，压缩 trigram postings 已写入 v4 snapshot，但重启后 `containsPostingsLoaded=False`，说明 postings 恢复链路还没有真正生效。

## 启动阶段对比

| 阶段 | 优化前 v3 | 优化后 v4，无 postings | 优化后 v4，含 postings 文件 | 变化 |
| --- | ---: | ---: | ---: | --- |
| Snapshot 文件大小 | 244.86MB | 179.88MB | 319.74MB | 目录-only 后下降；加入 postings 后回升 |
| FRN entries | 3,810,788 | 716,813 | 716,813 | 降低约 81% |
| Snapshot load | 2712-2864ms | 2067ms | 2096ms | 约 22-28% 改善 |
| Snapshot restore | 10970-11564ms | 9352ms | 8796ms | 约 15-24% 改善 |
| Restore total | 13836-14278ms | 11421ms | 10894ms | 约 21-24% 改善 |
| USN catch-up 查询日志读取 | expired 或 0-1ms | 3-6ms/卷 | 5-6ms/卷 | 日志读取很快 |
| USN apply 总耗时 | 无有效追平样本 | 5975ms | 5824ms | 当前最大冷启动阻塞点 |
| Watcher start | 无有效样本 | 66ms | 340ms | 可接受 |
| Contains warmup | 基线无独立压缩桶 | 23538ms | 启动后仍重建 | 后台耗时长，且会干扰冷启动 |

## 搜索耗时对比

| 场景 | 优化前 Host(ms) | 优化后 InProcess 稳态 Host(ms) | 优化后 SharedHost 冷启动 Host(ms) | 说明 |
| --- | ---: | ---: | ---: | --- |
| Desktop `d` | 56 | 94 | 6108 | 冷启动被 USN apply 锁阻塞；contains 自身 8-16ms |
| Desktop `ve` | 36 | 8 | 939 | 冷启动前几次请求受 apply/warmup 干扰 |
| Desktop `v -> ve -> ver` | 37 / 32 / 35 | 8 / 2 / 1 | 1355 / 4 / 3 | 增量缓存稳态有效 |
| Desktop `*.exe` | 31 | 2 | 5 | 稳态明显改善 |
| Global `d` | 457 | 42 | 401 | InProcess 稳态大幅改善；SharedHost 冷启动仍接近旧值 |
| Global `ve` | 225 | 34 | 194 | 同上 |
| Launchable `workbench` | 2-3 | 4 | 6 | 基本持平 |

## 路径前置

| 指标 | 优化前 | 优化后稳态 | SharedHost 冷启动 |
| --- | ---: | ---: | ---: |
| Desktop path prefilter 首次 | 26-40ms | 29-74ms | 5314-5809ms |
| Desktop path prefilter 后续命中 | 0-30ms | 0ms | 0ms |
| Desktop 候选数 | 38133 | 38133 | 38133 |
| Desktop 目录数 | 3286 | 3286 | 3286 |

路径前置功能本身是有效的：第一次解析后缓存命中为 `0ms`，候选集稳定缩小到 `38133`。SharedHost 的 5 秒尖刺来自并发 USN apply 期间的锁竞争，不是路径算法本身。

## Contains 与缓存

| 指标 | 优化前 | 优化后 InProcess | 优化后 SharedHost 冷启动 |
| --- | ---: | ---: | ---: |
| Path contains miss 平均 | 约 5-9ms（路径内） | 6-16ms | 8-16ms |
| Path incremental hit | 0-1ms | 0-1ms | 1ms |
| Global `d` contains verify | 422-455ms | 40ms | 300-401ms |
| Global `ve` contains verify | 214-223ms | 32ms | 187-210ms |

结论：当前低对象开销扫描在 InProcess 稳态非常有效；SharedHost 冷启动期间退化明显，主要来自 CPU/锁竞争和后台 warmup/catchup 并发。

## 内存对比

| 样本 | Working Set | Private |
| --- | ---: | ---: |
| 优化前 MftScanner | 5172.1MB | 5955.4MB |
| 优化后 SharedHost v3->v4 首轮测试后 | 2107.6MB | 2149.4MB |
| 优化后 SharedHost v4 重启测试后 | 2167.2MB | 2180.2MB |
| 优化后 SharedHost v4+postings 测试后 | 2311.1MB | 2337.6MB |
| 优化后 warmup 后二次观察 | 约 2091.9MB | 2105.6MB |

内存目标第一阶段基本达成：从约 `6GB` 降到约 `2.1-2.3GB`。但距离 Everything 级别仍很远，主要差距仍在 `FileRecord/string/数组/托管索引结构`，还没有进入真正 compact record store 阶段。

## Snapshot 与桶持久化

| 项 | 结果 |
| --- | --- |
| v4 snapshot 保存 | 成功 |
| directory-only FRN snapshot | 成功，FRN entries 从 381 万降到 71.7 万 |
| snapshot save 耗时 | 从约 5-6s 降到约 2.2s |
| compressed trigram postings 写入 | 文件大小从 179.9MB 增至 319.7MB，说明写入了额外数据 |
| compressed trigram postings 加载 | 未成功，日志仍为 `containsPostingsLoaded=False` |

postings 加载失败是当前必须修的点。最可能原因是保存 snapshot 时 `Records.Length` 与 postings 的 `RecordCount` 因 USN overlay/增量变更不一致，加载保护拒绝使用。结果是每次重启仍会后台重建 trigram，耗时约 `23.5s`。

## 当前最大问题排序

1. USN catch-up apply 阻塞搜索：`applyMs=5558-5967ms`，导致首个路径前置查询 5 秒级。
2. postings snapshot 未成功加载：`containsPostingsLoaded=False`，冷启动仍重建 trigram。
3. SharedHost 冷启动全局 contains 仍偏慢：`d=300-401ms`、`ve=187-210ms`，明显差于 InProcess 稳态。
4. 内存虽然从 6GB 降到 2.1-2.3GB，但还未达到 1GB 内目标。
5. v4 postings 文件 319.7MB，说明压缩有效但仍占用较大；后续需要 block-level metadata 和 mmap/arena 方向继续压缩。

## 建议的下一轮优化

1. 把 USN catch-up apply 拆成后台批量合并，搜索优先读旧 snapshot + 小 overlay，避免启动后 5-6 秒锁住查询。
2. 修复 postings snapshot 的 recordCount/epoch 一致性：保存前冻结 records 与 accelerator base，或者保存 postings 时带 base epoch 并在加载时重建 overlay。
3. 路径子树缓存预热：snapshot restore 后预热最近路径或 Desktop/Home，避免首次 path prefilter 与 catch-up 争锁。
4. SharedHost benchmark 增加阶段指标：`snapshotLoadMs/restoreMs/catchupReadMs/catchupApplyMs/warmupMs/postingsLoaded/pathColdMs/pathWarmMs`。
5. 继续推进 compact record store，减少 `FileRecord` 对象和字符串引用，才可能从 2GB 级继续降到 1GB 内。
