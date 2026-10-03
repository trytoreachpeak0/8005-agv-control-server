# 批次 8：等待点登记与多车启动校验

批次8-17（control-server#388，REQ-0289、REQ-0297，规格 5.4）。写给现场部署与运维：多车部署为什么要先导入等待点才能启动、
登记文件在哪、怎么导入、导入之后能改什么、改了对在途的车有什么影响。

## 一、合入之后的部署变化：多车部署要先导入等待点

**从这个版本起，`JourneyRuntime:Fleet` 多于一台车的服务端，只有在等待点登记能给每辆车各分一个本图启用的等待点时才启动。**
否则进程在监听之前退出，日志里是一条 `WAITING_POINTS_FEWER_THAN_VEHICLES`，例如：

```
Waiting point registration refused: WAITING_POINTS_FEWER_THAN_VEHICLES JourneyRuntime:Fleet has 2 vehicles, but waiting point
registration (none imported) gives only 0 of them a waiting point of their own on Map 26 (0 enabled there). Short by 2; left
without a point: <agv02 的 VehicleKey>, <agv03 的 VehicleKey>. No database edit is needed. Either register or enable at least
2 more waiting point(s) on Map 26 that these vehicles' whitelists admit, with the server stopped, then start it again:
ControlServer.FieldOps.exe import-waiting-points --database "<服务端实际打开的库文件>" --input <waiting-points.csv>
--catalog <stations-26.json> --map 26 --fleet "<agv02 的 VehicleKey>;<agv03 的 VehicleKey>" (add --dry-run to preview;
read-waiting-points shows the current registration). Or take vehicles out of JourneyRuntime:Fleet until the registration
covers the rest (0 can be covered now; one vehicle is not checked). See docs/field/batch-8-waiting-point-registration.md.
A single-vehicle deployment (JourneyRuntime:Fleet empty or one vehicle) is not subject to this check.
```

意思是：名册里有 2 台车，但登记（这里是一版都没导入）在 26 号图上一个点都分不出来，还差 2 个，两台车都没有着落。**两条出路都不需要改库**：

- 补登记：报错里已经把命令拼好了，数据库路径是服务端实际打开的那个文件、`--map` 与 `--fleet` 取自服务端自己的配置，只要补上登记文件（第二节）
  与站点目录（第三节）两个路径，先加 `--dry-run` 看一眼，再去掉它导入，然后重启服务。
- 或者从 `JourneyRuntime:Fleet` 里摘掉车，直到登记够剩下的车用；只剩一台时不校验。

**点登记在别的图上时，报错不给现成命令。**例如服务端还配着 25 号图、点登记在 26 号图，报错会写
「Registered on other Maps, which this server does not run: Map 26 x3」，然后先让人核对 `JourneyRuntime:mapId`
（「First check JourneyRuntime:mapId (this server runs Map 25) ...」），只有车真的跑在这张图上才去这张图登记。原因是：
照抄一条 `--map 25` 的命令，会把等待点登记到 MVP 与 agv01 所在的 25 号图上，而那里根本没测绘过等待点。

**报错说某个等待点「不算」，是因为它是任务类型固定站。**报错会写明它出自哪一版绑定集、那一版有没有生效，例如
`305 (binding set version 2, latest, not active)`。启动校验、导入与承诺前的判定都把**生效版本与最新一版（哪怕还没激活）**里的站当固定站：
批次 6 的激活分两步，第一步写下的那一版已经是「马上要成为固定站」的站。所以**一次没激活成功的绑定，会让它的站不算等待点**。这种情况用
`rollback-task-type-stations --map 26 --version <当前生效版本> --catalog <stations-26.json> --reason <原因>` 清掉：回滚把生效那一版的内容再写成一个
新版本并激活（不是拨回指针），那个没激活的版本就不再是最新，它的站也就不再算固定站。反过来，如果那一版本来就该生效，就把它激活。

注意：回滚与激活都要求带来的目录是**运行中的服务端刚确认过**的那一份（批次 6 的新鲜度规则），而多车服务端被拒启动时不会去确认目录。
所以这种情况下的顺序是：先把 `JourneyRuntime:Fleet` 改成只剩一台车启动（单车不校验），等服务端确认过目录后回滚或激活，
再恢复车队、重启。全程不用改库。

为什么这样定：规格 5.4 的理由是桩少于车时会互锁——一辆无处可去的车会占着另一辆车要用的点。单车部署（`Fleet` 为空或只有一台）不校验，
行为与升级前完全相同。

**并行期（约 10-08 起）的 v2 实例如果按 agv02、agv03 两台车部署，升级到含本票的版本之前要先导入登记文件**，否则升级后起不来。
升级顺序：

1. 停服务（或者就让它在新版本上起一次、被拒）。服务端每次启动都先迁移数据库，所以被拒的那一次也会把库建好、迁移到新版本。
   `ControlServer.Host.exe --migrate-only` 只迁移建库然后退出（不开监听端口、不连 RIoT、不读 MES、不建单），只给 L2 编排器用。
   **现场不要单独运行它**：服务端取不到连接串时用默认库 `%ProgramData%\8005\ControlServer\data\controlserver.db`，那是 MVP 生产库；
   并行实例的连接串只在它安装目录的 `appsettings.Production.json` 里、按当前目录读，从别的目录（例如 `ssh factory01` 进去的用户主目录）
   运行，退出码照样是 0，迁移的却是 MVP 的库。若确需，只能在并行实例安装目录里运行，或把
   `--ConnectionStrings:ControlServer "Data Source=<并行实例的库>"` 放在 `--migrate-only` **之前**，并核对迁移出的是哪个文件
   （批次9-02 审查实测，control-server#400）。
2. 导出 26 号图的站点目录（见第三节），用 FieldOps 导入登记文件（见第四节）。服务端停着也能导。
3. 启动服务。

**服务端所跑的图要是 26 号图。**v2 出厂的 `appsettings.json` 里 `JourneyRuntime:mapId` 仍是 25（切到 26 属切生产配置，规格第 21.5 节），
而等待点建在 26 号图上。启动校验**只数本服务端所跑那张图上的启用点**：一个仍配 25 号图的多车部署，即使 26 号图上登记了三个点也会被拒，
日志会写出 `Registered on other Maps, which this server does not run: Map 26 x3`，并先让人核对 `JourneyRuntime:mapId`。
这不是登记错了，是部署配置还没切到 26。

## 部署机上的工具从哪来

**发布包里没有 FieldOps。**`New-WireToGateReleaseCandidate.ps1` 打的包只有服务端、看板与车载端；而本票之后，多车部署在启动之前必须先用
FieldOps 导入等待点。所以部署一台多车服务端时，要随包一起带三样东西过去：

1. **`ControlServer.FieldOps.exe`**：在控制端笔记本上，从**与部署的服务端同一个提交**的干净检出里发布（发布包的
   `release-manifest.json` 记着服务端提交）。版本要一致，因为 FieldOps 直接写服务端的库，库结构随提交走。
   ```powershell
   git -C <control-server 检出> checkout <服务端提交>
   dotnet publish <control-server 检出>/tools/ControlServer.FieldOps/ControlServer.FieldOps.csproj -c Release -r win-x64 --self-contained true -o C:/8005/fieldops-<提交前 8 位>
   ```
   与服务端包一样自带运行时（`win-x64`、`--self-contained true`），部署机上不用另装 .NET。
2. **`scripts/field/Export-RiotStationCatalog.ps1`**（同一个检出里）。它要 pwsh 7（厂区各机器都是 7.6.5）、能直连 RIoT
   `172.19.206.222:8888`、以及环境变量 `CONTROL_SERVER_RIOT_CALL_API_KEY`。用 `scripts/Install-ControlServerLocal.ps1` 安装、且没加
   `-SkipMachineEnvironmentInjection` 的服务端，会把它写进部署机的 Machine 范围；别的装法（例如并行期另起的 v2 实例）以部署机实际环境为准。
   也可以在控制端笔记本上跑（那里的 User 范围也有这把 key），导出后把 `catalog-26.json` 一起拷过去。
3. **`docs/field/waiting-points-map26.csv`**（同一个检出里）。

三样都经控制端中转拷到部署机（与服务端包同一条路：`scp` 到部署机上 FieldOps 的目录），不要让部署机自己去拉仓库。

## 二、登记文件

仓里的现场登记文件是 [`waiting-points-map26.csv`](waiting-points-map26.csv)：26 号图上的站 214「等待点1」、215「等待点2」、
216「等待点3」，全部启用，白名单为空（同图全部车辆都能停）。三个点对应用户 2026-09-28 口述的现场已建情况；站名与坐标经过一次只读核对，
结论记在 control-server#388 的评论里。

格式是受控 CSV：UTF-8（Excel 的「CSV UTF-8」带 BOM 也收），表头逐字是

```
map_id,station_id,station_name,enabled,vehicle_scope
```

- `station_name` 必须与 RIoT 目录里的站名逐字一致；
- `enabled` 只写 `true` 或 `false`；
- `vehicle_scope` 是白名单，写车辆的 `VehicleKey`，多个用分号隔开；留空表示同图全部车辆开放（`REQ-0289` 的默认）。

**一张表是这张图的全部等待点。**表里没列的点即删除；别的图上的登记不受影响。

## 三、导出站点目录（只读）

FieldOps 够不到 RIoT，站点目录由运维带进来。用仓里的只读脚本导出：

```powershell
pwsh -File scripts/field/Export-RiotStationCatalog.ps1 -MapId 26 -OutFile C:/8005/catalog-26.json
```

它只对 RIoT 发一个 GET（`/api/imap/v1/mapInfo/stations/26`），用服务端同一把 `CONTROL_SERVER_RIOT_CALL_API_KEY`，不重试、不写 RIoT。
除了 `--catalog` 要的那份文件，它还在旁边留一份原始应答 `catalog-26.raw.json`，里面有每个站的坐标（`pos.x`、`pos.y`；RIoT 实际返回的字段名带点，不是 OpenAPI 里写的 `posX`）——服务端读不到坐标，
所以「等待点不得与业务站或充电站共享物理坐标」（`REQ-0289`）只能由人对着这份原始应答看。

目录**不要求**是服务端最近一次确认过的那一份：多车服务端在导入之前起不来，也就不会去确认目录。导入结果里的
`catalogMatchesServerConfirmation` 只告诉你两者是否一致（`null` 表示服务端从没确认过这张图）。运行时服务端会再按实时目录判一次：
不在实时目录里、或站名对不上的等待点不接新承诺。

## 四、导入与查看

先预演，看它会做什么，一行不写：

```powershell
ControlServer.FieldOps.exe import-waiting-points --database <服务端的 controlserver.db> `
    --input docs/field/waiting-points-map26.csv --catalog C:/8005/catalog-26.json `
    --map 26 --fleet "<agv02 的 VehicleKey>;<agv03 的 VehicleKey>" --dry-run
```

- `--map` 是服务端所跑的图（`JourneyRuntime:mapId`），`--fleet` 是投运名册里全部车辆的 `VehicleKey`（照部署配置 `JourneyRuntime:Fleet` 抄，
  分号隔开，PowerShell 里要加引号）。FieldOps 不读设置文件，这两项要人写出来；服务端启动时会按自己的配置再判一次。
- 去掉 `--dry-run` 就是真导入。输出一段 JSON：`outcome` 为 `OK`（写成了新版本）、`UNCHANGED`（与当前版本相同，没出新版本）、
  `REJECTED`（整份拒绝，一行没写，退出码 1）或 `CONFLICT`（另一次导入先提交了，本次整体回滚，读一下当前版本再决定要不要重导）。
- `coverage` 写这支车队在这张图上够不够：`sufficient` 为 `false` 时 `warning` 会说「下一次启动会被拒」。

拒绝时每类错误一个原因码，一次全列出来：

| 原因码 | 意思 |
| --- | --- |
| `WAITING_POINTS_CSV_HEADER_INVALID` | 表头不是上面那五列 |
| `WAITING_POINTS_CSV_ROW_MALFORMED` | 字段数不对、带首尾空白、数字或 `true`/`false` 写法不对、白名单写法不对、表中间有空行 |
| `WAITING_POINT_MAP_MISMATCH` | 行的图号或目录的图号不是 `--map` |
| `WAITING_POINT_STATION_NOT_IN_CATALOG` | 站不在带来的目录里 |
| `WAITING_POINT_STATION_NAME_MISMATCH` | 站名与目录不一致 |
| `WAITING_POINT_IS_MACHINE_STATION` | 站是机台站（站名能读出 AREA，例如 `N1-3_N1-7`） |
| `WAITING_POINT_IS_FIXED_TASK_STATION` | 站是某个任务类型绑定的固定站（批次 6 的绑定） |
| `WAITING_POINT_STATION_HAS_OTHER_ROLE` | 站名含「关卡」或「充电」：210「关卡」、211「充电点1」、212「充电准备点1」这类，规格 5.4 点名 212 不得登记 |
| `WAITING_POINT_STATION_DUPLICATED` | 同一个站在表里出现了不止一次 |
| `WAITING_POINT_VEHICLE_OUTSIDE_FLEET` | 白名单里有 `--fleet` 之外的车 |

只读查看当前（或某一版）登记，给了 `--map` 与 `--fleet` 时附上覆盖：

```powershell
ControlServer.FieldOps.exe read-waiting-points --database <controlserver.db> --map 26 --fleet "<VehicleKey>;<VehicleKey>"
ControlServer.FieldOps.exe read-waiting-points --database <controlserver.db> --version 1
```

## 五、停用、删除、改白名单：只影响新承诺

每次导入都是一个新版本，带治理快照与业务审计，旧版本原样保留。

**停用、删除、改角色、收窄白名单，只让这个点不再接新的承诺。**已经在点上或正在去这个点的车不受影响：它的预占或占用记录照旧引用它当时的
登记版本，保留到车辆安全离点并完成对账（`REQ-0297`）。导入不取消订单、不释放任何独占、不让第二辆车进入。

预演与导入的输出里 `retainedReferences` 会把这样的记录逐条列出：哪个站、哪辆车、哪趟旅程、预占还是在点、依据的是哪一版登记、
新版本下为什么不再接它（停用、未登记、白名单不含它），以及那句「保留到车辆安全离点并完成对账」。

**运行中导入一个让点数少于车辆数的版本，服务端不会自己停下，也不会拒收这次导入。**规格 5.4 写的是「拒绝以该配置启动」；一个坏掉的点
必须停得掉。这样的导入会在 `coverage.warning` 里写明，服务端**下一次启动**会被拒。

## 六、这一票不做的

空闲返回（车空闲时去等待点）的资格与承诺、默认开关归批次8-18（control-server#389）；独占的预占、转占用与释放归批次8-19
（control-server#390）。所以**本票合入之后，登记了等待点也不会让任何车移动**：除了启动校验，服务端运行时还不读登记。

## 七、单已完成，车却证明不了到点：等待点到点人工收尾（control-server#447）

### 什么时候用

开往等待点的单有两种：空闲返回（车没活时自己回等待点），和清桩开往等待点（充不上电的车从桩上开去等待点）。RIoT 报这张单已完成之后，
服务端还要读到车**在线、空闲、速度为零、身上没有任务、读数新鲜，并且当前地图加当前站正好是那个等待点**，才算到点、才收尾。

有时这一条一直满足不了：RIoT 报的地图与服务端配置的对不上，车停偏了停在相邻的站上，或读数一直不新鲜。服务端**不会因为等久了就放车、放点**，
车会一直挂着这趟没收尾的旅程，派车一直绕开它。看板上这时显示：

| 码 | 在哪 |
| --- | --- |
| `IDLE_RETURN_ARRIVAL_NOT_PROVEN` | 空闲返回旅程；车队视图「空闲返回」里这一步叫 `ARRIVAL_NOT_PROVEN` |
| `CHARGING_CLEARANCE_ARRIVAL_NOT_PROVEN` | 充电旅程（清桩中开往等待点） |

持续 10 分钟（`JourneyRuntime:OwnOrderRebuildRepeatWindow`）会在服务端日志里告警一次：空闲返回是事件 2230，清桩是事件 2303。
**过了这 10 分钟才能用这个入口**，之前办会被拒（`ARRIVAL_SETTLEMENT_TOO_EARLY`）。

### 用之前在现场核实什么

1. **车停在哪里。**站在车旁边看：它是不是正好停在那个等待点上（结论 `AT_WAITING_POINT`），还是停在别处（`NOT_AT_WAITING_POINT`）。
   拍照或记巡检单号，填进 `siteVerification`。
2. **车已经停稳、没在执行任务。**服务端自己也会读 RIoT 核对，但人先看一眼：车灯、急停、RIoT 里这辆车身上有没有单。
3. **车在线。**车离线或 RIoT 读不到它时入口一律拒绝（`ARRIVAL_SETTLEMENT_VEHICLE_OFFLINE`、`ARRIVAL_SETTLEMENT_VEHICLE_UNREADABLE`）：
   服务端证明不了它没在动，就不收尾。**先让车重新上线、停稳，再来办。**
4. **清桩那一种，车确实在等待点上而清桩还没完成时**：先由 R-11／R-13 名单里的人确认清桩（人工清桩入口），再办这一项。
   不先确认清桩会被拒（`ARRIVAL_SETTLEMENT_CLEARANCE_STILL_OPEN`）。
5. **对照看板，抄下这辆车当前旅程的旅程号和它正开往的等待点站号。**

### 谁能办、怎么办

只有 R-11／R-13 名单里的人（与人工清桩同一份名单，`FieldOperatorRoles:path`）。入口与故障人工恢复同一把凭据、同一个开关
（`VehicleFaultRecovery:enabled`），在服务端的健康端口上：

```powershell
$body = @{
    agvId            = 'agv02'
    journeyId        = '<看板上这辆车当前旅程的旅程号>'
    stationId        = 214                       # 它正开往的那个等待点的 RIoT 站号
    verdict          = 'AT_WAITING_POINT'        # 或 NOT_AT_WAITING_POINT
    operatorId       = '<名单里的工号>'
    reason           = '车停在等待点上，RIoT 报的地图不对'
    siteVerification = '<照片编号或巡检单号>'
} | ConvertTo-Json
Invoke-RestMethod -Method Post -Uri 'http://127.0.0.1:58007/api/field-ops/v1/waiting-point-arrival-settlements' `
    -Headers @{ Authorization = "Bearer $env:CONTROL_SERVER_FAULT_RECOVERY_CREDENTIAL" } `
    -ContentType 'application/json' -Body $body
```

### 办完之后

| 结论 | 空闲返回 | 清桩开往等待点 |
| --- | --- | --- |
| `AT_WAITING_POINT` | 按到点收尾：车占着这个等待点，用途释放，车回到可派。旅程码 `IDLE_RETURN_ARRIVAL_CONFIRMED_BY_OPERATOR` | 按到点收尾：车占着这个等待点，旅程以清桩收尾码收尾 |
| `NOT_AT_WAITING_POINT` | 按已确认失败结束（`IDLE_RETURN_NOT_AT_WAITING_POINT_BY_OPERATOR`）：用途释放，等待点在车被读到停在别的站后由离点清扫释放；下一次空闲返回不选这个点，冷却与「10 分钟内两次就停止自动空闲返回」照算 | 这次移动结束、等待点当场释放，车回到清桩中，**这一次清桩不会再自动出发**，要人工清桩 |

每一次请求，办成的和被拒的，都写一条管理员审计（动作 `WAITING_POINT_ARRIVAL_SETTLEMENT`），记下办理人、角色、理由、核实记录和这一刻读到的
RIoT。被拒时返回 409，`codes` 列出全部原因，`descriptions` 是每个原因的中文说明，照着处理之后再提交一次。
返回 `ARRIVAL_SETTLEMENT_STATE_CHANGED`，说明核对期间服务端刚推进过这趟旅程，或别人刚办过：什么也没写，看一眼看板再提交。
