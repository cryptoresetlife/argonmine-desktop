# Argonmine Desktop

Windows NVIDIA GPU SOLO miner with a graphical interface, a bundled local Argonmine node, automatic GPU detection and automatic mining after synchronization.

面向新手的 Windows 图形界面矿工：填写自己的 ARG 收款地址，点击开始，本地节点同步并通过检查后自动开挖。**第三方实验项目，非 Argonmine 官方发行版。**

## 下载与使用

从 [Releases](https://github.com/cryptoresetlife/argonmine-desktop/releases) 下载 `Argonmine-Desktop-0.2.2-Windows-NVIDIA.zip`，完整解压后双击 `ArgonmineDesktop.exe`。

1. 填自己的 **ARG 主网收款地址**，不需要私钥、助记词或钱包文件。
2. 软件自动识别 NVIDIA 显卡，显示型号和显存；多卡时可选择一张。
3. 选择“日常使用”，点击“开始 · 同步后自动挖矿”。
4. 等待主网校验、连接、同步和区块新鲜度连续两次检查通过，再由矿工做 CPU/GPU 一致性自检。
5. 点击停止或关闭窗口，将停止本软件启动的节点和矿工，并取消等待中的自动开挖。

重新打开软件不会自动开挖，需要再次点击开始。已有矿工须先停止，避免显卡争用。

## 功能与兼容范围

- 自动检测 NVIDIA 型号、显存和 UUID；优先恢复之前选择的卡，首次按显存选择默认卡。
- 编译目标覆盖 GTX16、RTX20/30/40/50 系及兼容专业卡，最低 CUDA 计算能力 **7.5**、至少 **2GB** 显存。
- 需要 Windows 10/11 x64、.NET Framework 4.8、兼容 CUDA 13.3 的 NVIDIA 驱动；运行发布包不需要自行安装 CUDA Toolkit 或 Go。
- GTX10、Maxwell、Pascal、Volta 不支持当前 CUDA 13 构建。AMD、Intel、macOS 不支持。
- 本界面一次运行所选的一张卡，尚未提供多卡同时开挖。
- 日常模式：显存预算为总显存的35%，最多4GiB，80%批次计算占空比。
- 高性能模式：总显存的60%，最多16GiB，100%占空比。批次向下取2的幂，开挖时按空闲显存再次缩减。
- 高性能模式可能影响桌面响应；满载开发测试曾发生卡死，建议先用日常模式。
- 显示本地块高、连接节点、平均算力、候选提交数与日志。不读取显卡温度，不修改功耗、频率或风扇。

**验证范围：**已在本机 RTX5090 验证静态检测。地址校验、同步条件、显存档位、开始后立即停止、进程隔离退出测试通过；本地节点在 REGTEST 上完成正常关机测试。其他显卡尚未逐型号实卡验收，检测通过不等于已验证兼容，每次开挖须先通过 CPU/GPU 自检。v0.2.2 已在本机 RTX5090 日常模式完成实挖启动验收，短样本约45k H/s；这不代表其他显卡已验收或长期收益保证。

原始部署在 RTX5090 高性能档实测约84.8–86k H/s，并产生过有效主网区块；这些数据不能作为本发行版或其他显卡的性能、收益保证。

## 数据与安全边界

- 仅使用用户填写的收款地址，不包含任何默认奖励收款人，也不托管钱包。
- 本机数据在 `%LOCALAPPDATA%\ArgonmineDesktop`，RPC只监听 `127.0.0.1:19352`。不要映射RPC到公网，不要分享`rpc.cookie`。
- 不设置开机自启、计划任务或异常自动重启；遇到 `HALTED` 应先看日志，不自动清除异常保护。
- 只管理本软件创建的进程组，不接管或停止其他矿工。
- “官方网页（邀请链接）”只在点击时打开维护者的邀请链接，`ref`不作为本地矿工的收款地址。
- 自行核验下载校验值和源码；本项目的校验清单不是数字签名。

## SOLO 与奖励

本项目使用本地节点进行 SOLO 挖矿。候选提交、日志或算力不等于最终收益，必须核对主链区块及coinbase收款地址。当前50 ARG补贴按协议给国库0.5 ARG、矿工49.5 ARG，另加可归矿工的交易费；没有本软件额外开发者抽水。Coinbase须经过100块成熟期才能花费，转账另扣手续费。

查询：[官方浏览器](https://explorer.argonmine.com/)。规则和实际收益以当前主网为准。

## 编译

需要 **Go 1.27、CUDA Toolkit 13.3、Visual Studio 2022 Build Tools C++**、Windows自带.NET Framework编译器。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

CUDA脚本默认使用官方工具安装路径；安装位置不同时修改`source/build-worker.cmd`。Go构建须使用`-mod=readonly`，因为`source/vendor`保存CUDA第三方源码，并不是Go的vendor目录。

构建生成根目录`ArgonmineDesktop.exe`、`bin/`和`binaries.json`。运行：

```powershell
.\ArgonmineDesktop.exe --self-test
.\ArgonmineDesktop.exe --ui-self-test
```

这两个检查不启动GPU挖矿，也不启动主网节点。GPU离线共识测试需要空闲显卡和`ARGON_GPU_WORKER`环境变量；不要与实挖同时运行。REGTEST结果不属于主网收益。

## 源码来源与许可证

保留全部上游版权声明。许可证为混合组成，不能把整个CUDA worker当作MIT代码：

| 组成 | 来源与版本 | 许可证 |
|---|---|---|
| Argonmine 节点及Go基础代码 | [argonchain-source/argonchain](https://github.com/argonchain-source/argonchain)，`e428841b44320fe3a2096c137ae044a2edc0b9f1` | MIT，见`source/LICENSE` |
| Argon2 GPU基础代码 | [WebDollar/argon2-gpu](https://github.com/WebDollar/argon2-gpu)，`f5fce223d16769f202449c9b0837ad3533304acd` | fork顶层为MIT，但继承文件含GPL-2.0-or-later声明；组合CUDA worker按GPL-2.0-or-later分发 |
| 本地GUI、检测器、控制器和修改 | 本仓库 | 保留本地MIT许可及上游声明；与GPU代码组合时遵循GPL-2.0-or-later |

根目录`LICENSE`提供GPLv2完整文本，`licenses/`与源文件保留其他许可。发布包随附对应源码与构建说明。节点只增加可选的`-stopfile`正常退出标记，没有修改共识或P2P规则。

欢迎通过 Issues 提供显卡型号、驱动版本和去除敏感信息后的报错。**不要上传助记词、私钥、cookie或钱包文件。**

## v0.2.2 修复
修复 getpeerinfo 的嵌套 JSON 数组在 .NET 中反序列化为 ArrayList，而界面误强转为 object[] 的问题。旧版会在同步完成后一直显示等待节点响应。新增实际响应格式回归测试、实时只读 RPC 测试、错误去重显示及开挖前停止标记检查。

