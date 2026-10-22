# 第三方组件与许可证（Attribution）

DevUp 的界面与业务代码为自有实现；下列开源组件以 **NuGet 引用** 或 **模块内嵌** 的方式复用，
版权归原作者所有，均以 MIT 许可证分发。此文件随软件一同分发，用于满足 MIT 许可证的署名要求。

---

## 1. Nefarius.Utilities.DeviceManagement 6.1.0

- 项目主页：<https://github.com/nefarius/Nefarius.Utilities.DeviceManagement>
- 作者：Nefarius Software Solutions e.U.
- 许可证：MIT
- 本项目用途：封装 SetupAPI / CfgMgr32 / DriverStore API，用于枚举设备、读取 DriverStore 驱动包信息。
- 复用方式：`PackageReference`（NuGet），未修改源码。

```
MIT License

Copyright (c) Nefarius Software Solutions e.U. and contributors

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

---

## 2. PSWindowsUpdate 2.2.1.5

- 项目主页：<https://github.com/mgajda83/PSWindowsUpdate>
- 作者：Michal Gajda
- 许可证：MIT（<https://github.com/mgajda83/PSWindowsUpdate/blob/main/LICENSE>）
- 本项目用途：调用 Windows Update Agent（WUA）搜索、下载、安装驱动更新。
- 复用方式：模块文件原样内嵌于 `lib/PSWindowsUpdate`（含 `PSWindowsUpdate.dll` 与帮助 XML），
  运行时释放到运行时目录后由 `devup.ps1` 导入，未修改源码。
- 版权声明：`(c) 2011-2024 Michal Gajda. All rights reserved.`

```
MIT License

Copyright (c) 2022 Michal Gajda

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

---

## 3. MSCatalog 0.27.0

- 项目主页：<https://github.com/ryan-jan/MSCatalog>
- 作者：Ryan Kowalewski
- 许可证：MIT
- 本项目用途：检索 Microsoft Update Catalog（catalog.update.microsoft.com）上的离线更新包。
- 复用方式：内嵌 `lib/MSCatalog` 整个模块目录（含 `Public/`、`Private/`、`Classes/` 与
  `Types/` 下的 HtmlAgilityPack），运行时释放后由 `devup.ps1` 导入，未修改源码。
- 版权声明：`(c) 2019 Ryan Kowalewski. All rights reserved.`

```
MIT License

Copyright (c) 2019 Ryan Kowalewski

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

---

## 4. winget（Windows 程序包管理器）

- 项目主页：<https://github.com/microsoft/winget-cli>
- 作者：Microsoft Corporation
- 许可证：MIT
- 本项目用途：获取已安装软件清单、比对可用版本、读取官方清单中的发行说明 / 安装包地址 / SHA256、执行升级与下载。
- 复用方式：调用系统已安装的 `winget.exe` 命令行（`winget upgrade`、`winget show`、`winget download` 等），不随包分发其二进制文件。
- 校验值说明：界面展示的 SHA256 来自 winget 官方源清单；MD5 由 DevUp 在本地对下载文件计算，仅用于与官方 SHA256 交叉核对与留档。

---

## 5. 系统自带组件

以下能力由 Windows 自带命令 / 接口提供，不涉及第三方许可：

- `pnputil.exe` —— 驱动包导入 / 导出
- `Checkpoint-Computer` —— 系统还原点
- `Get-PnpDevice` / CIM `Win32_PnPEntity` / `Win32_PnPSignedDriver` —— 设备与驱动枚举
- WUA（Windows Update Agent）COM 接口
- `WinVerifyTrust`（wintrust.dll）—— Authenticode 签名校验

---

## 6. 未使用的项目说明

本项目的设计思路参考了同类开源工具（如 SDIO 等）的公开文档与交互方式，但**未复制其源码**；
SDIO 等项目采用 GPLv3 许可证，本项目为避免许可证传染，仅自行实现相似功能。

---

最后更新：2026-10-22
