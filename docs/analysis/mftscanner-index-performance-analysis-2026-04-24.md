# MftScanner 索引与包含查询性能分析报告

## 1. 文档信息

- 文档名称：MftScanner 索引与包含查询性能分析报告
- 分析日期：2026-04-24
- 分析范围：索引加载、索引恢复、桶构建、USN 追平、单字符串与多字符包含查询
- 分析方式：基于真实调试日志 + 代码实现路径交叉分析

## 2. 数据来源

本报告基于以下真实文件：

- 调试日志：`C:\Users\AustinYan\AppData\Local\PackageManager\logs\debug\20260424.log`
- 索引性能日志：`C:\Users\AustinYan\AppData\Local\PackageManager\logs\index-service-diagnostics\20260424.log`
- 配置文件：`C:\Users\AustinYan\AppData\Roaming\PackageManager\settings.json`
- 索引快照：`C:\Users\AustinYan\AppData\Roaming\PackageManager\MftScannerIndex\index.bin`

本报告同时参考以下实现代码：

- `Tools/MftScanner.Core/IndexService.cs`
- `Tools/MftScanner.Core/MemoryIndex.cs`
- `Tools/MftScanner.Core/IndexPerfLog.cs`

## 3. 先说结论

当前索引服务的主性能策略是正确的，整体路径为：

1. 优先从快照恢复索引，尽快达到“可搜索”
2. 在后台执行 USN 追平，补齐快照与当前文件系统之间的差量
3. 在后台异步构建 contains 加速桶，避免拖慢首轮可用时间

从本次真实日志看，当前阶段的核心结论如下：

- 首次可搜索时间的主要开销在快照文件加载与反序列化，不在内存恢复本身
- USN 追平采集变化本身很快，主要开销在把变化批量应用到内存索引
- contains 加速桶构建总耗时明显高于快照恢复，因此延后到后台执行是合理的
- 单字符查询是理论上的最差场景，因为候选集最大
- 二字符查询性能取决于 bigram 热度，像高频组合仍可能命中较大桶
- 三字符及以上查询整体最稳定，trigram 交集策略效果明显
- 当前用户体感里的“搜索慢”，很多时候不是索引匹配慢，而是 IPC 与 UI 链路更慢

## 4. 当前样本环境概况

根据快照与日志，本次样本的大致规模如下：

- 快照文件大小：约 `77.08 MB`
- 快照记录数：`1,126,684`
- 快照卷数：`3`
- 字符串池数量：`644,040`
- FRN 映射数：`1,164,903`

这说明当前分析样本已经是百万级记录场景，具备一定代表性。

## 5. 加载索引与恢复索引分析

### 5.1 真实日志

```text
[SNAPSHOT LOAD] hit elapsedMs=3636 version=3 fileBytes=77076490 records=1126684 volumes=3 frnEntries=1164903 stringPool=644040
[SNAPSHOT RESTORE] elapsedMs=659 records=1126684 volumes=3
[SNAPSHOT RESTORE TOTAL] totalMs=4297 loadMs=3636 restoreMs=659 restoredCount=1126684
[BUILD INDEX ASYNC] success elapsedMs=4366 indexedCount=1126684
```

### 5.2 结论

- 本次启动命中了快照，没有走全量 MFT 重建
- 快照加载耗时 `3636 ms`
- 内存恢复耗时 `659 ms`
- 总恢复耗时 `4297 ms`

从占比上看：

- `SNAPSHOT LOAD` 约占总恢复时间的 `84.6%`
- `SNAPSHOT RESTORE` 约占总恢复时间的 `15.4%`

### 5.3 性能判断

这说明当前“恢复慢”的主要来源不是内存结构重建，而是：

- 快照文件读取
- 快照对象反序列化
- 大对象数组装载

而恢复阶段本身只做了：

- 恢复卷快照信息
- 加载排序后的记录数组
- 不构建 contains 加速桶

这一点和代码实现一致：

- `TryRestoreFromSnapshot(...)` 中调用 `_index.LoadSortedRecords(snapshot.Records, buildContainsAccelerator: false)`

即恢复阶段明确采用“先可搜，再补桶”的策略。

## 6. 桶构建（Contains Warmup）分析

### 6.1 真实日志

```text
[CONTAINS WARMUP] outcome=start reason=post-catchup records=1126688
[CONTAINS WARMUP] outcome=stage reason=post-catchup stage=trigram-ready elapsedMs=5975 records=1126689
[CONTAINS WARMUP] outcome=success reason=post-catchup elapsedMs=17054 records=1126689
```

### 6.2 结论

- trigram 阶段完成耗时约 `5.975 s`
- 全量 contains warmup 完成耗时约 `17.054 s`

### 6.3 性能判断

contains 桶构建明显比快照恢复更重。如果把它放在快照恢复之前或同步执行，会直接拖慢“首轮可搜索时间”。  
当前的实现方式是：

- 先恢复排序索引
- 再后台追平 USN
- 然后后台 warmup trigram / full buckets

这个顺序是合理的，属于典型的“可用优先”策略。

另外，从日志上可以看出 trigram 优先就绪，这对三字符及以上查询帮助最大，也说明当前 warmup 有意识地优先保障中长关键词查询体验。

## 7. USN 追平分析

### 7.1 真实日志

```text
[SNAPSHOT CATCHUP] drive=C elapsedMs=7 startUsn=153158050856 nextUsn=153158176576 journalId=132808133984029427 changes=321
[SNAPSHOT CATCHUP] drive=D elapsedMs=5 startUsn=3936630728 nextUsn=3936630728 journalId=132973975091847387 changes=0
[SNAPSHOT CATCHUP] drive=E elapsedMs=1 startUsn=1982053040 nextUsn=1982053040 journalId=132973975095478203 changes=0
[SNAPSHOT CATCHUP TOTAL] catchUpMs=1051 applyMs=1041 totalChanges=321 watcherStartMs=135 indexedCount=1126688
```

### 7.2 结论

- C 盘追平采集耗时 `7 ms`，共 `321` 条变化
- D、E 盘基本无变化
- 总追平耗时 `1051 ms`
- 其中批量应用变化耗时 `1041 ms`

### 7.3 性能判断

本轮 USN 追平的瓶颈不在 Journal 读取，而在变化应用阶段。也就是：

- `_enumerator.ApplyUsnChanges(allChanges)`
- `_index.ApplyBatch(allChanges, rebuildContainsAccelerator: false)`

换句话说：

- “发现变化”很快
- “把变化同步进当前索引”才是主要耗时点

不过这部分工作运行在后台，不阻塞首轮搜索，因此从用户体验角度仍然是可接受的。

## 8. 全量 MFT 构建分析

### 8.1 本次样本状态

本次日志没有出现真实的全量成功日志，例如：

```text
[MFT BUILD] success totalMs=...
```

因此本报告不能给出本机本次“全量扫盘”的真实定量耗时。

### 8.2 代码路径判断

从实现上看，全量构建流程为：

1. 获取所有固定盘
2. 多卷并行枚举 MFT
3. 合并所有卷记录
4. 构建主索引结构
5. 启动 watcher
6. 异步保存快照
7. 异步 warmup contains buckets

也就是说，全量构建阶段同样采用了“主索引先完成，contains 桶后补”的策略。

### 8.3 当前结论边界

本报告只能确认全量构建的策略与埋点位置，不能伪造其真实耗时。  
如果后续要拿到完整冷启动数据，需要主动采集一轮：

- 无快照启动
- 快照失效重建
- 多盘大改动后的重建

## 9. 单字符串与多字符包含查询分析

## 9.1 当前实现机制

当前 contains 查询并不是“多个独立关键词分词求交”，而是“一个查询串按长度走不同加速路径”：

- 长度 `1`：`char bucket`
- 长度 `2`：`bigram bucket`
- 长度 `>= 3`：`trigram bucket + 交集 + LowerName.Contains 二次验证`

这意味着：

- 单字符查询最容易命中超大桶
- 双字符查询性能受 bigram 热度影响较大
- 三字符及以上查询更容易快速收敛候选集

## 9.2 真实样本统计

| 查询词 | 模式 | 匹配数 | hostSearchMs | totalUiSearchMs | 结论 |
| --- | --- | ---: | ---: | ---: | --- |
| `w` | char | 249,923 | 3-10 ms | 26-61 ms | 最差路径，候选极大 |
| `we` | bigram | 55,478 | 2-3 ms | 35 ms | 收敛明显优于单字符 |
| `wx` | bigram | 342 | 1 ms | 27 ms | 较快 |
| `wdx` | trigram | 3 | 1-2 ms | 268 ms | 索引快，慢在 IPC/UI |
| `aw` | bigram | 12,288 | 0-1 ms | 28 ms | 可接受 |
| `duo` | trigram | 188 | 1 ms | 17 ms | 很稳 |
| `config` | trigram | 9,480 | 4 ms | 26 ms | 稳定 |
| `我` | char | 22 | 1 ms | 18 ms | 稀疏字符很快 |
| `帝国` | bigram | 138 | 1 ms | 22 ms | 很快 |
| `大秦` | bigram | 138 | 1 ms | 20 ms | 很快 |

## 9.3 单字符查询分析

以 `w` 为例：

```text
[CONTAINS QUERY] outcome=success mode=char filter=All candidateCount=249923 intersectMs=0 verifyMs=0 matched=249923 keyword=w
[SEARCH] outcome=success totalMs=3 matchMs=2 resolveMs=0 ... returned=500 truncated=True
```

可以得出：

- 单字符直接命中 `char bucket`
- 候选数达到 `249,923`
- 当前数据规模下，内存匹配阶段仍只用了 `2-3 ms`

这说明当前实现虽然在理论上是最差路径，但在百万级数据下仍然可控。  
不过它仍然是未来最容易退化的点，因为：

- 候选桶随数据规模扩张最明显
- 高频英文字母命中面最大
- 后续如增加更重的过滤条件，单字符会最先暴露问题

## 9.4 二字符查询分析

以 `we` 为例：

```text
[CONTAINS QUERY] outcome=success mode=bigram filter=All candidateCount=55478 intersectMs=0 verifyMs=0 matched=55478 keyword=we
[SEARCH] outcome=success totalMs=2 matchMs=1 resolveMs=0 ... returned=500 truncated=True
```

说明：

- bigram 已经明显比单字符更能缩小范围
- 但高频 bigram 仍可能落在数万级候选

以 `wx` 为例：

```text
[CONTAINS QUERY] outcome=success mode=bigram filter=All candidateCount=342 ...
```

说明：

- 二字符查询的波动很大
- 高频组合和稀疏组合的性能差异显著

## 9.5 三字符及以上查询分析

以 `config` 为例：

```text
[CONTAINS QUERY] outcome=success mode=trigram filter=All candidateCount=9480 intersectMs=1 verifyMs=1 matched=9480 keyword=config
[SEARCH] outcome=success totalMs=4 matchMs=3 resolveMs=0 ...
```

可以看出：

- trigram 交集耗时 `1 ms`
- 二次 `Contains` 验证耗时 `1 ms`
- 最终 host 侧搜索总耗时仅 `4 ms`

以 `wdx` 为例：

```text
[CONTAINS QUERY] outcome=success mode=trigram filter=All candidateCount=3 intersectMs=0 verifyMs=0 matched=3 keyword=wdx
[SEARCH] outcome=success totalMs=1 ...
[IPC] ... waitMs=180 totalMs=181
[UI] ... totalUiSearchMs=268
```

这说明：

- trigram 路径本身非常快
- 实际慢感主要不在匹配，而在跨进程等待和 UI 更新

因此，从纯搜索算法角度讲，三字符及以上查询是当前最优路径。

## 10. UI 感知性能与索引性能的关系

从本次日志可以确认一个很重要的点：  
“用户感觉慢”不一定等于“索引搜索慢”。

典型例子是 `wdx`：

- host 搜索只用了 `1-2 ms`
- IPC 却用了 `181 ms`
- UI 总耗时达到了 `268 ms`

这说明当前链路里至少有三段成本：

1. 索引匹配
2. 宿主进程 IPC
3. UI 投影、绑定与线程调度

所以如果后续继续做性能优化，不能只盯着 `MemoryIndex.Search(...)`。  
对于用户体感，以下两项同样关键：

- IPC 等待时间
- UI 线程调度与结果绑定

## 11. 当前架构的优点

从本轮日志看，当前架构具备以下明显优势：

- 快照恢复显著缩短了启动后的首轮可搜索时间
- USN 追平走后台，不阻塞先搜
- contains warmup 走后台，不阻塞恢复
- trigram 优先就绪，对中长关键词搜索非常友好
- 结果分页只取前 `500` 条，避免首屏结果投影过重

这些点都说明当前架构已经具备“先可用，再追平，再提速”的正确方向。

## 12. 当前可能的性能风险

虽然当前整体表现不错，但仍有几个风险点值得关注：

### 12.1 快照加载仍然偏重

从 `4297 ms` 总恢复时间来看，最大的单项开销仍是快照加载。  
后续如果记录数继续增长，快照体积和反序列化时间还会继续增加。

### 12.2 单字符高频查询仍是最差路径

当前 `w` 命中近 `25` 万候选仍能跑得动，但它本质上仍然是候选最泛的一条路径。  
后续数据继续膨胀后，这里最有可能最先出问题。

### 12.3 追平应用成本高于采集成本

USN 采集很快，但 apply 阶段接近 `1 s`。  
如果单次追平变化数进一步增大，这部分可能继续上升。

### 12.4 用户体感可能更多卡在 IPC/UI

从 `wdx` 的样本看，索引层其实已经很快，但 UI 总耗时仍然明显。  
这意味着后续如果继续优化用户体感，需要一并看：

- 跨进程通信等待
- UI 线程排队
- 列表绑定和投影成本

## 13. 可以确认但不能过度解读的点

以下结论可以确认，但不建议过度泛化：

- 本次样本中 trigram 查询很稳，不代表所有 trigram 查询都永远是常数级
- 当前 `char/bigram` 查询很快，不代表未来数据量翻倍后仍保持同样时间
- 当前 warmup 用时 `17 s`，不代表所有机器与所有索引规模都一致
- 当前没有全量 MFT 构建真实样本，因此不能用本报告评估“冷启动扫盘耗时”

## 14. 最终结论

本次真实日志说明，当前索引服务的性能结构是健康的：

- 恢复阶段的重点是快照加载，而不是内存恢复
- 快照恢复后的首轮可搜索时间是可接受的
- USN 追平的采集很轻，真正花时间的是批量应用
- contains warmup 明显较重，放后台是正确选择
- 单字符查询是理论最差路径，但当前仍可接受
- 三字符及以上查询的性能最稳，收益最大
- 用户体感中的延迟，很多时候不在索引层，而在 IPC 与 UI 链路

如果后续要继续做专项优化，建议优先关注：

1. 快照加载与反序列化时间
2. USN 变更批量应用效率
3. IPC 等待和 UI 绑定耗时
4. 高频单字符查询在更大数据量下的退化趋势

## 15. 后续建议采样项

为了让后续分析更完整，建议补采三类数据：

1. 无快照冷启动样本，获得真实全量 MFT 构建时间
2. 大批量 USN 变更样本，观察 apply 阶段增长曲线
3. 单字符查询的连续翻页样本，评估高 offset 下的稳定性

这样后续就可以从“结构性判断”进一步升级到“定量基准报告”。
