# ASF-TimedPlay

给 [ArchiSteamFarm](https://github.com/JustArchiNET/ArchiSteamFarm) 加一条 `playfor` 命令:**启动指定游戏,挂满指定时长后自动停掉。**

ASF 原生只有 `pause& <秒>`(到点自动恢复)和 `play`(手动模式,会被各种情况打断),没有"挂满 N 分钟就停住"的语义。这个插件补上这块,并且解决了 `play` 在真实环境里几乎必然被打断的两个问题(见下文「稳定性设计」)。

---

## 安装

**1.** 从 [Releases](https://github.com/Savues/ASF-TimedPlay/releases) 下载 `TimedPlay.zip`

**2.** 解压到 ASF 目录下,得到 `plugins/TimedPlay/TimedPlay.dll`

**3.** 重启 ASF,日志出现这两行就成功了

```
InitPlugins() 正在加载 TimedPlayPlugin V1.1.1.0……
InitPlugins() TimedPlayPlugin 加载成功！
```

插件只有一个 DLL,无任何外部依赖(`System.Composition.*`、`NLog` 等全部由 ASF 自己提供)。

> ⚠️ 请使用 **generic 版** ASF。单文件 / 自包含版理论上对插件支持较差,不过只要该安装能加载其它第三方插件,本插件同样能加载 —— 它只用到了 MEF,没碰任何可能被裁剪的 API。

---

## 命令

```
playfor|f [Bots] <AppIDs> [Minutes] [GameName]  启动游戏,Minutes 分钟后自动停止(省略则 35 分钟)
playfor|f [Bots] status                         查看剩余时间与心跳状态
playfor|f [Bots] cancel                         取消计时(游戏继续跑)
```

`f` 是 `playfor` 的单字母别名,两者完全等价。

| 参数 | 说明 |
|---|---|
| `[Bots]` | 可选。支持 `@all` / `@farming` / `@idle`、`A..C` 区间、正则,与 ASF 内置命令完全一致。省略则作用于发出命令的 bot |
| `<AppIDs>` | 支持 `730`、`app/730`、`a/730`,多个用逗号分隔,最多 32 个(Steam 网络硬限制) |
| `[Minutes]` | 可选,省略即 **35 分钟**。支持小数,`0.5` = 30 秒,`1800` = 30 小时。上限 43000 分钟(约 29.8 天) |
| `[GameName]` | 可选,作为 Steam 好友看到的状态文字 |

因为时长可省略,`[Minutes]` 和 `[GameName]` 之间靠类型区分:**紧跟 AppIDs 的第一个 token 如果能解析成数字,就是时长,否则就是游戏名的开头。**

```
!f 730                        → 730,挂 35 分钟
!f 730 Hollow Knight          → 730,挂 35 分钟,显示名 "Hollow Knight"
!f 730 35 Hollow Knight       → 730,挂 35 分钟,显示名 "Hollow Knight"
```

代价是:**游戏名以数字开头时必须把时长写出来**,例如 `!f 730 35 2 Fast 2 Furious`。同理,把时长手滑写成非数字会被当成游戏名,然后按默认 35 分钟跑。

Steam 聊天需加 `!` 前缀;IPC 模式不需要。

### 自动执行 `std`

插件加载后每 **10 分钟**自动对所有已登录的 bot 执行一次 `std` 命令(SteamTokenDumperPlugin 的手动刷新),驱动令牌数据更及时地提交。
执行结果会写入 ASF 日志,关键字 `[TimedPlay] auto std`。
SteamTokenDumperPlugin 服务端有 5 分钟提交限流,高频触发是安全的,未到间隔的提交会被自动跳过。

### 例子

```
!f 730                                  CS2 挂 35 分钟(默认时长)
!f 730 30                               CS2 挂 30 分钟
!playfor 730 0.5                        挂 30 秒(适合测试)
!f app/730,440 45                       两个游戏并行挂 45 分钟
!f app/730 30 Counter-Strike            带自定义显示名
!f 730 Hollow Knight                    默认 35 分钟 + 显示名
!playfor @all app/730 30                所有 bot 同时挂
!playfor A..C app/730 30                区间
!f status                               查剩余时间
!f cancel                               撤销计时,游戏不停
```

---

## 给插件开发者的坑:ASF 会裁剪 BCL

如果你以后要改这个插件,**这条务必先读**。

ASF 的单文件发行版是**裁剪(trimmed)**构建的,而且只保留 ASF 自己(以及官方插件)真正调用过的 BCL 成员 —— 它有个 `TrimmerRoots.xml` 白名单,里面逐个方法地列出必须保留的东西,注释直接写着 "Required for SteamTokenDumper plugin" 之类:

```xml
<assembly fullname="System.Linq">
    <type fullname="System.Linq.Enumerable">
        <method name="MaxBy" />
    </type>
</assembly>
```

**你插件里用到的、ASF 自己没用到的 BCL 重载,会被直接删掉**,而且只在代码真正执行到那一行时才炸,表现为一个莫名其妙的:

```
System.MissingMethodException: Attempted to access a missing method.
   at YourPlugin.YourClass.SomeMethod(...)
```

本插件踩过的坑,以及改成的写法:

| ❌ 会被裁掉 | ✅ 换成 ASF 自己在用的 |
|---|---|
| `double.TryParse(s, NumberStyles, CultureInfo, out double)` —— ASF 从不用 double 解析 | `float.TryParse(s, NumberStyles, CultureInfo, out float)` |
| `uint.TryParse(s, NumberStyles, CultureInfo, out uint)` —— ASF 只用 2 参数版 | `uint.TryParse(s, out v)` |
| `string.Join('+', xs)` char 分隔符 | `string.Join("+", xs)` |
| `Interlocked.Exchange<T>()` | 普通赋值(`Timer.Dispose()` 本身是幂等的) |
| `ReferenceEquals(a, b)` | 用现有的 `IsActive` 状态判断 |
| `string.Join(string, params string[])` —— 例如 `string.Join(" ", 某个 string[])` | 强制走存活的 `IEnumerable<string>` 重载:`string.Join(" ", (IEnumerable<string>) arr)`,或先塞进 `List<string>` |
| `array[start..]`(带**显式起点**的切片) —— 编译成 `System.Range.StartAt` | `Substring(start)` / `Substring(start, length)` |

后两条是 v1.1.1 的翻车现场,特别隐蔽:

- `string.Join` 的 `params string[]` 重载和 `IEnumerable<string>` 重载**同名同参数量**,肉眼看签名几乎一样,但只有后者被保留。
- `x[..n]`(省略起点)不会用到 `Range.StartAt`,`x[n..]`(显式起点)才会 —— 所以同一个文件里一处能跑一处炸。

### 别再靠人肉核对了

`tools/ilcheck` 是一个 IL 级检查器:它读插件的调用点,再跟**裁剪后实际存活**的 BCL 成员逐一比对,直接告诉你哪几个被删了:

```powershell
# 需要一个裁剪过的 ASF 目录(含 System.Private.CoreLib.dll,framework-dependent 发行版没有)
dotnet publish <ASF 源码>\ArchiSteamFarm\ArchiSteamFarm.csproj -c Release -r win-x64 `
    --self-contained true -p:PublishTrimmed=true -p:PublishSingleFile=false -o trimmed

.\build.ps1
.\check-trimming.ps1 -TrimmedDirectory .\trimmed
```

CI 里已经接上了这一步,所以**这类 bug 以后会在构建期被拦下,而不是等用户装完才炸**。

判据是 `存活的定义 ∪ ASF 自己的调用点` —— ILLink 会保留 App 真正需要的一切,所以 ASF 自己调用过的东西必然存活。

这个 bug 在开发机上(未裁剪)完全复现不出来,只在用户装上之后才炸 —— 本插件的 v1.1.0 和 v1.1.1 都是这么翻车的。

---

## 为什么不用原生 `play`

原生 `play` 有两个隐蔽的坑,实战中几乎必然导致挂机时长为 0。

### 坑一:挂载会被 ASF 自己清空

`Bot.CheckOccupationStatus()` 一旦被触发就会调用 `ResetGamesPlayed()`,后者发送一个**空的**游戏列表,等于把手动的 `play` 抹掉。这个方法会在以下情况被调用:

- Steam 下发 `PlayingSessionStateCallback`(`PlayingBlocked` 状态发生变化时)
- Steam 下发 `SharedLibraryLockStatusCallback`
- 任何 bot 执行 `reset` 命令

触发时机完全由 Steam 决定,ASF 自己都不知道,日志里只会看到一句「帳號不再被佔用,已恢復掛卡!」。

### 坑二:断线后永不恢复

Steam 连接断开时,ASF 重连后**不会恢复**之前挂的游戏。实测环境中断线一次就足以让整个会话作废:

```
12:49:43  play asf 4235370          ← 开始挂
12:53:52  已與 Steam 中斷連線！      ← 断了
12:54:01  已與 Steam 中斷連線！      ← 8 秒内又断
12:54:22  帳號不再被佔用，已恢復掛卡！ ← 游戏被清空
```

这次会话实际只挂了 **4 分 9 秒**,而 Steam 每约 30 分钟才批量结算一次时长,所以结果就是 0。

### 坑三:`Actions.Pause()` 根本不会停游戏

到点停止时最自然的写法是 `bot.Actions.Pause(permanent: true)`,但它是**停不了游戏的**(`ArchiSteamFarm/Steam/Interaction/Actions.cs`):

```csharp
public async Task<(bool Success, string Message)> Pause(bool permanent, ushort resumeInSeconds = 0) {
    if (Bot.CardsFarmer.Paused) {
        return (false, Strings.BotAutomaticIdlingPausedAlready);   // 直接返回,什么都没做
    }

    await Bot.CardsFarmer.Pause(permanent).ConfigureAwait(false);

    if (!permanent && !Bot.BotConfig.GamesPlayedWhileIdle.IsEmpty) {
        await Bot.ArchiHandler.PlayGames([], Bot.BotConfig.CustomGamePlayedWhileIdle).ConfigureAwait(false);
        //  ↑ 只有非 permanent 分支才清空游戏列表
    }
```

两个问题叠在一起:

1. `playfor` 一开始就调了 `Actions.Play()`,它内部会 `Pause(true)` —— 所以**会话期间 farmer 永远是 paused 状态**,到点时 `Pause` 第一行就短路返回。
2. 就算没短路,`permanent: true` 分支本来就不含 `PlayGames([])`。

结果就是**游戏从头跑到尾,一次都没停过**,而日志还会打出一条看起来像失败的:

```
playfor: 4235370 finished on Savues but pausing failed - 已經暫停自動掛卡！
```

真正能清场的只有一条路:`Actions.Play([])` —— 空集合会走到 `ArchiHandler.PlayGames([])`,和 ASF 自己清场用的是同一个调用。本插件因此改成**只用 `Actions.Play([])` 停游戏**,完全不碰 `Pause()`(见下一节)。

---

## 稳定性设计

**1. 心跳(每 2 分钟)**

定时重新补发游戏列表,被 `CheckOccupationStatus()` 清掉就自动挂回去。

**2. 断线重连自动恢复**

实现 `IBotConnection`,在 `OnBotLoggedOn` 时立即恢复挂载。断线频繁的环境下这条是刚需。

**3. 挂卡状态原样还给你**

这是插件唯一允许自己**暂时**改变挂卡的理由:用户主动让 bot 挂游戏,账号被 Steam 判定为「占用中」,挂卡必然要暂停,这是绕不开的。

除此之外插件不再改动挂卡。做法是会话开始时记下 `bot.CardsFarmer.Paused`,到点停完游戏后:

- 会话前**没**暂停 → 调 `Actions.Resume()`。这一步是必须的,因为 `Actions.Play()` 会设 `PermanentlyPaused`,而 ASF 自己的自动恢复(`CheckOccupationStatus` → `Resume(false)`)碰到永久暂停会直接拒绝,挂卡就再也回不来了。
- 会话前**已经**暂停(你自己 `pause` 过)→ 什么都不做,保持暂停。

实测:

```
# 会话前未暂停
playfor: 4235370 finished on Savues, games stopped, cards farming 已繼續自動掛卡！
# 会话前已暂停
playfor: 4235370 finished on Savues, games stopped, cards farming left paused as it was
```

**会话期间 `resume` 会被覆盖**:心跳调用 `Actions.Play()`,会重新暂停挂卡。也就是说 `playfor` 会话期间 bot 归插件管,直到计时结束。想提前交还控制权用 `playfor cancel`(注意 `cancel` **不会**停游戏,游戏继续跑,挂卡也就继续保持暂停)。

---

## 排查:挂机时长不涨

按顺序排查:

**① Steam 是批量结算的,约 30 分钟一次。** 官方 FAQ 原文:

> It does, but **not in real-time**... it's usually updated every 30 minutes or so.

低于 30 分钟的会话很可能结算为 0。验证时请让会话**连续跑满 35 分钟以上**,中途不要碰 `reset` / `resume`。

**② 检查断线。** 搜索日志里的 `已與 Steam 中斷連線`。每次断线都会清空挂载。

**③ 检查 `SteamProtocols`。** 默认值是 `7`(TCP + UDP + WebSocket 全开)。如果改成了 `4`(仅 WebSocket),意味着**全部 Steam 流量都走 `WebProxy` 代理** —— 代理一抖就是断线。建议改回默认,或至少 `3`。

**④ 确认游戏有许可。** `play` 完全不校验所有权,直接把 appID 发给 Steam。账号没有该游戏的话 Steam 不会计任何时长。

**⑤ 配了 `GamesPlayedWhileIdle` 的话**,停止挂卡后 ASF 会改去玩那批游戏,不是完全停止。想彻底停就把它留空。

---

## 从源码构建

需要 .NET 10 SDK(ASF 6.3.10.3 的目标框架是 `net10.0`)。

```powershell
git clone https://github.com/Savues/ASF-TimedPlay.git
cd ASF-TimedPlay
.\build.ps1
```

`build.ps1` 会自动下载 ASF generic 发行版并提取编译所需的三个引用程序集到 `lib\`(该目录已 gitignore,不会被提交),然后输出到 `bin\Release\net10.0\TimedPlay.dll`。

可选参数:

```powershell
.\build.ps1 -AsfVersion 6.3.10.3              # 固定 ASF 版本,默认取最新 release
.\build.ps1 -AsfDirectory C:\path\to\ASF     # 直接用现成的 ASF 安装目录
.\build.ps1 -Configuration Debug             # Debug 构建
```

发布 Release 用 `release.ps1`,它全程走 GitHub REST API,**不需要 `git push`**(Git 的 smart-HTTP 传输在某些网络下会被拦或超时):

```powershell
.\release.ps1 -Version 1.4.0
```

脚本依次做这几件事:把 `-Version` 写进 `TimedPlay.csproj` → 构建 → 打包 `TimedPlay.zip`(内含 `plugins/TimedPlay/TimedPlay.dll`、`LICENSE`、`README.md`)→ 通过 API 把当前工作树镜像成远端默认分支上的一个提交 → 打 tag → 建 Release 并上传附件。远端分支始终等于当前工作树,不依赖本地提交历史。

Token 的取值顺序是 `-Token` 参数 → `$env:GITHUB_TOKEN` → git 为 github.com 保存的凭据。

```powershell
.\release.ps1 -Version 1.4.1 -SkipBuild   # 复用 bin 里已有的 DLL,不重新构建
```

CI(`.github/workflows/ci.yml`)只在 `main` 与 PR 上跑构建和裁剪检查,不再负责发布。

---

## 兼容性

| 项目 | 值 |
|---|---|
| 最低 ASF 版本 | 6.3.10.3 |
| 目标框架 | net10.0 |
| ASF 变体 | generic 必需,OS-specific 可能可用 |
| 插件依赖 | 无 |

---

## License

[MIT](LICENSE) © 2026 Savues

## 免责声明

ASF 官方明确说明:**自定义插件完全不受官方支持**,加载后即视为脱离 vanilla ASF,出现问题请自行排查。
