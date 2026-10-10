# ASF-TimedPlay

给 [ArchiSteamFarm](https://github.com/JustArchiNET/ArchiSteamFarm) 加一条 `playfor` 命令:**启动指定游戏,挂满指定时长后自动停掉。**

ASF 原生只有 `pause& <秒>`(到点自动恢复)和 `play`(手动模式,会被各种情况打断),没有"挂满 N 分钟就停住"的语义。这个插件补上这块,并且解决了 `play` 在真实环境里几乎必然被打断的两个问题(见下文「稳定性设计」)。

---

## 安装

**1.** 从 [Releases](https://github.com/Savues/ASF-TimedPlay/releases) 下载 `TimedPlay.zip`

**2.** 解压到 ASF 目录下,得到 `plugins/TimedPlay/TimedPlay.dll`

**3.** 重启 ASF,日志出现这两行就成功了

```
InitPlugins() 正在加载 TimedPlayPlugin V1.1.0.0……
InitPlugins() TimedPlayPlugin 加载成功！
```

插件只有一个 DLL,无任何外部依赖(`System.Composition.*`、`NLog` 等全部由 ASF 自己提供)。

> ⚠️ 请使用 **generic 版** ASF。单文件 / 自包含版理论上对插件支持较差,不过只要该安装能加载其它第三方插件,本插件同样能加载 —— 它只用到了 MEF,没碰任何可能被裁剪的 API。

---

## 命令

```
playfor [Bots] <AppIDs> <Minutes> [GameName]    启动游戏,Minutes 分钟后自动停止
playfor [Bots] status                           查看剩余时间与心跳状态
playfor [Bots] cancel                           取消计时(游戏继续跑)
```

| 参数 | 说明 |
|---|---|
| `[Bots]` | 可选。支持 `@all` / `@farming` / `@idle`、`A..C` 区间、正则,与 ASF 内置命令完全一致。省略则作用于发出命令的 bot |
| `<AppIDs>` | 支持 `730`、`app/730`、`a/730`,多个用逗号分隔,最多 32 个(Steam 网络硬限制) |
| `<Minutes>` | 支持小数,`0.5` = 30 秒,`1800` = 30 小时。上限 43000 分钟(约 29.8 天) |
| `[GameName]` | 可选,作为 Steam 好友看到的状态文字 |

Steam 聊天需加 `!` 前缀;IPC 模式不需要。

### 例子

```
!playfor app/730 30                    CS2 挂 30 分钟
!playfor 730 0.5                       挂 30 秒(适合测试)
!playfor app/730,440 45                两个游戏并行挂 45 分钟
!playfor app/730 30 Counter-Strike    带自定义显示名
!playfor @all app/730 30               所有 bot 同时挂
!playfor A..C app/730 30               区间
!playfor status                        查剩余时间
!playfor cancel                        撤销计时,游戏不停
```

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

---

## 稳定性设计

**1. 心跳(每 2 分钟)**

定时重新补发游戏列表,被 `CheckOccupationStatus()` 清掉就自动挂回去。

**2. 断线重连自动恢复**

实现 `IBotConnection`,在 `OnBotLoggedOn` 时立即恢复挂载。断线频繁的环境下这条是刚需。

**3. 会话期间 `resume` 会被覆盖**

刻意的设计:心跳调用 `Actions.Play()`,会重新暂停挂卡。也就是说 `playfor` 会话期间 bot 归插件管,直到计时结束。想提前交还控制权用 `playfor cancel`。

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

打 tag 即可触发 CI 自动构建并发布 Release:

```powershell
git tag v1.2.0
git push origin v1.2.0
```

CI 会用 tag 的版本号覆盖程序集版本号,并把打包好的 `TimedPlay.zip` 挂到 Release 上。

---

## 兼容性

| 项目 | 值 |
|---|---|
| 最低 ASF 版本 | 6.3.10.3 |
| 目标框架 | net10.0 |
| ASF 变体 | generic 必需,OS-specific 可能可用 |
| 插件依赖 | 无 |

---

## 免责声明

ASF 官方明确说明:**自定义插件完全不受官方支持**,加载后即视为脱离 vanilla ASF,出现问题请自行排查。
