# XIVLauncherCN (Soil) [![Actions Status](https://img.shields.io/github/actions/workflow/status/AtmoOmen/FFXIVQuickLauncher/ci-workflow.yml?branch=CN)](https://github.com/AtmoOmen/FFXIVQuickLauncher/actions) [![Discord Shield](https://discordapp.com/api/guilds/1258981591124938762/widget.png?style=shield)](https://discord.gg/dailyroutines) [![GitHub all releases](https://img.shields.io/github/downloads/AtmoOmen/FFXIVQuickLauncher/total)](https://github.com/AtmoOmen/FFXIVQuickLauncher/releases/latest) [![GitHub release (latest by date)](https://img.shields.io/github/v/release/AtmoOmen/FFXIVQuickLauncher)](https://github.com/AtmoOmen/FFXIVQuickLauncher/releases/latest) <a href="https://github.com/AtmoOmen/FFXIVQuickLauncher/releases"><img src="https://github.com/AtmoOmen/FFXIVQuickLauncher/raw/CN/src/XIVLauncher/Resources/logo.png" alt="XL logo" width="100" align="right"/></a>

XIVLauncherCN (Soil) 是 最终幻想14 非官方启动器 XIVLauncherCN 的分支

注: 本项目完全不使用 ottercorp 的服务器资源, 全部使用 Github 或者 公益 CDN 实现

<p align="center">
  <a href="https://github.com/goatcorp/FFXIVQuickLauncher/releases">
    <img src="https://raw.githubusercontent.com/goatcorp/FFXIVQuickLauncher/master/misc/screenshot.png" alt="drawing" width="500"/>
  </a>
</p>

## 与 XIVLauncherCN 的区别 [信息可能滞后]

- 删除了 Dalamud 启动时的游戏版本检测
- 删除了所有的数据上报逻辑
- 删除了国服 Dalamud 的插件封禁逻辑
- 一大堆鸡零狗碎的界面、逻辑优化等等...

## 我也想自己维护, 怎么操作

请关注以下仓库, 修改其中所有涉及到 `AtmoOmen` 的链接、工作流和脚本 (当然你也可以不改, 都是走 GitHub)

- [XIVLauncherCN (Soil)](https://github.com/AtmoOmen/FFXIVQuickLauncher): 在 IDE 内全局查找 `AtmoOmen` 并进行替换
- [XLCNSoilAssets](https://github.com/Dalamud-DailyRoutines/XLCNSoilAssets): 内含完整的维护更新指引, 可以继续使用或者自己维护
- [Dalamud (Soil)](https://github.com/AtmoOmen/Dalamud): 在 IDE 内全局查找 `AtmoOmen` 并进行替换
- [DalamudAssets](https://github.com/AtmoOmen/DalamudAssets): 修改 `hash.py` 文件, 修改为原始的 Github 链接或使用自己的反代服务。其中, `asset.json` 中各个文件的 hash 会在 push 时由工作流直接更新, `Version` 则需要手动修改 —— 除非是 breaking changes, 否则正常情况下确实不需要更改 `Version`

## 免责声明
XIVLauncher 并不符合 Square Enix 的服务条款。 我们已经尽力地确保使用 XIVLauncher 对所有人来说都是安全的，且目前还没有玩家因此被封禁，但我们不能否认它存在的可能性。<br>您可以在[此处](https://goatcorp.github.io/faq/xl_troubleshooting#q-are-xivlauncher-dalamud-and-dalamud-plugins-safe-to-use)查到有关的信息。

## 特别鸣谢
<a href="https://www.jetbrains.com/community/opensource/#support">
   <img src="https://resources.jetbrains.com/storage/products/company/brand/logos/jb_beam.png" alt="JetBrains" width="200px" height="200px"><br/>

   <p>JetBrains OSS Sponsorship</p>
</a>


##### Final Fantasy XIV © 2010-2021 SQUARE ENIX CO., LTD. 保留所有权利。 我们不以任何形式附属于 SQUARE ENIX CO。
