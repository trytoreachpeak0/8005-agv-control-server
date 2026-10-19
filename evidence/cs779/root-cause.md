# cs#779 定因（2026-10-19，cs779 工作会话）

日志：factory01 `C:\ProgramData\8005\ControlServer.V2\logs\controlserver-20261019_002.ndjson`（只读拷到本机分析）。

## 结论：两次都不是「等写锁」，是驱动连接池把同一条底层连接同时借给了两个使用者

两次异常都抛在 `SqliteConnection.Open()` 里，位置是 `WireToGateStore.cs:2396` 那条去重查询（`SingleOrDefaultAsync`）的**开库**一步，事务还没开始，业务处理（responseFactory）一行没跑。

### 06:48:25Z `SQLite Error 5: 'database is locked ...'`

栈：`SqliteConnection.Open()` → `SqliteConnectionInternal.Close()` → `SqliteConnection.Deactivate()`。
读 Microsoft.Data.Sqlite 8.0.30 源码：Open 在 try 里给连接注册 EF 的函数（`sqlite3_create_function`），失败进 catch → `Close()` → 归还池 → `Deactivate()` 注销函数，又失败，抛出的就是这条（原始异常被盖掉）。

- `sqlite3_create_function` 返回 SQLITE_BUSY(5) 只有一种情况：同名函数已注册且**这条连接上有正在执行的语句**（"unable to delete/modify user-function due to active statements"）。它不碰数据库锁，busy timeout 不参与。
- 一条从池里正当借出的空闲连接不可能有活动语句（`Close()` 先 dispose 全部 command/reader）。所以这条连接当时**正被另一个使用者用着**。
- 报文文字是 `database is locked For more information...`（errstr + 默认后缀），说明 `ThrowExceptionForRC` 走了 `rc != sqlite3_errcode(db)` 分支：同一句柄的 errcode 在调用与检查之间被别人的 `sqlite3_step` 改掉了——又一条「另一个使用者同时在这条句柄上执行」的证据。

### 06:55:22Z `ObjectDisposedException 'SQLitePCL.sqlite3'`

栈：`SqliteConnection.Open()` → `sqlite3_create_function_v2`（SafeHandleAddRef 失败）。借到的底层连接的句柄**已经被释放**。正当流程里只有从池的两个栈都移除之后才会释放。符合「同一个内部连接在池里有两份」：一份被池的定时清理（2～4 分钟一轮，冷池里的直接 Dispose）释放，另一份随后被借出。它的 catch 里 `Close()` 因为 `_pool` 已被 Dispose 置空而直接 Dispose，不走 Deactivate，所以原始异常没被盖掉，与栈吻合。

### 上游

dotnet/efcore#39008「SQLite pool activation can lend the same connection to two live owners」：`SqliteConnectionInternal.Activate` 先置 `_active=true` 再设 owner，中间那一瞬 `Leaked==true`，另一个在池为空、`Count` 为偶数时开库的线程调 `ReclaimLeakedConnections()` 把它当泄漏连接归还并借走。修复 PR #39009（main）/#39012（release/10.0），**只进了 10.0.13，没有回补 8.0**。v8.0.30 源码 `Activate` 仍是旧顺序（已核）。8.0 的 `Deactivate` 里 `SetTarget(null)` 与 `_active=false` 之间有同样的窗口，且 8.0 的 `Return` 不在锁里。

### 排除项

- 长写事务占锁超过 30 秒：排除。失败点不在任何会等锁的调用上（开库注册函数），且两次都在事务开始之前。
- WAL 下延迟事务读升级写立即 BUSY：排除。EF 的 `BeginTransactionAsync` → Microsoft.Data.Sqlite `BeginTransaction(Unspecified)` → `deferred:false` → `BEGIN IMMEDIATE`（v8.0.30 `SqliteTransaction` 构造函数已核），一开始就拿写锁，没有升级这一步；而且失败点在事务之前。
- 外部读库：监控会话已停；且外部进程不共享本进程的连接池，造不出「同一句柄两个使用者」。
- 应用层共用同一个 `SqliteConnection` 对象或同一个 DbContext 跨线程：全仓只有 `UseSqlite(connectionString)`（按连接串，不传连接对象），没有 `new SqliteConnection`；每条车载连接一个 scope、严格串行（票面已核）。未发现。
- `ClearAllPools/ClearPool`：全仓无调用。

### 未能证实的部分

- 本机按上游复现程序（net8 + Microsoft.Data.Sqlite 8.0.30，200×16、1500×64 轮）**没有复现**出共享句柄（shared=0）。窗口只有两条赋值之间，本机没打中。所以「是 #39008 这个竞态」是由栈与源码推出的，不是复现出来的。程序在 `F:/8005-scratch/cs779-poolrace/`。
- 这两类异常在 10-17～10-19 全部 11 个日志文件里只出现在 v5 上线之后这 2 次。

### 数据写坏的可能（票面第 4 条）

- 这两次本身：失败的一方在开库就失败，没执行任何 SQL，入站事务没开始，没有写入；另一方的语句只被拒绝的函数注册打扰。**这两次没有写坏数据的迹象。**
- 但这个竞态一旦发生，两个使用者共用一条 SQLite 连接，就共用它的事务：一方的写入可能落进另一方的事务里，被对方回滚或提前提交。日志里没有 `within a transaction`/`no transaction is active` 一类报错（已 grep），但静默混写从日志看不出来。不能完全排除别处发生过一次未报错的共用。

## 14:58:02（06:58:02Z）1005 静默断连：与写库无关

- 前后没有任何数据库异常。
- 其他两车的 HTTP `vehicle-safety` 轮询（约 1 秒一次、20 ms 级）整段照常，服务端没有卡住。
- 第三条 HTTP 长连接 `0HNPDJO8DFKGM` 最后一次请求 06:57:53.812，之后中断，06:58:00.852 起换成新连接 `GO`/`GP`——与 agv01 TCP 会话「最后一条入站约 06:57:56.8、6 秒静默」同一时段。推断 agv01 那一侧（上位机或网络）两条通道同时中断约 7 秒，不是服务端在等库没读。
- 没读 agv01 车载日志（碰 agv01 属「先问」）。所以「GM 是 agv01」是按「三条 HTTP 连接、另两条照常」推断的。
