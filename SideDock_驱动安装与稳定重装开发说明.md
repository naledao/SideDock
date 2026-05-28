# SideDock 驱动安装与稳定重装开发说明

本文记录 SideDock 虚拟显示驱动的安装链路、旧驱动残留问题，以及更稳妥的重装策略。目标是让桌面端的“安装/修复驱动”按钮在大多数情况下都能完成一次可重复、可恢复的驱动安装。

## 1. 当前链路

当前桌面端入口是 `windows-host/SideDock.Host.App/MainWindow.xaml.cs` 里的“安装/修复驱动”按钮。它不会自己直接安装驱动，而是：

1. 寻找 `SideDock.Driver.Installer.exe`
2. 用管理员权限启动安装器
3. 安装器内部完成证书导入、旧工具进程停止、旧设备清理、旧驱动包清理、驱动安装、设备工具启动

安装器主体在 `windows-driver/SideDock.Driver.Installer/Program.cs`，核心步骤是：

1. 解包内置驱动载荷
2. 找到 `SideDock.Idd.inf`
3. 导入自签名证书到本机受信任存储
4. 停止已有的 `SideDock.Idd.DeviceTool.exe`
5. 执行 `pnputil /remove-device SWD\SideDockIdd\SideDockIdd`
6. 执行 `pnputil /enum-drivers /class Display /files`，识别并删除旧的 SideDock 驱动包
7. 执行 `pnputil /add-driver ... /install`
8. 启动 `SideDock.Idd.DeviceTool.exe`

桌面端文档里也已经写明了这个流程，见 `windows-driver/SideDock.Idd/README.md`。

## 2. 现有问题

`pnputil /add-driver ... /install` 并不等于“强制删除旧驱动再换新驱动”。如果机器里已经存在旧版本 SideDock 驱动，可能出现以下情况：

- 新 INF 没有更高的 `DriverVer`，Windows 可能认为它不是更新版本
- 旧的 `oemXX.inf` 仍留在 Driver Store 中
- 设备实例已重建，但实际绑定的还是旧包

所以，“点一次安装/修复”不一定等于真正的干净重装。

## 3. 稳妥方案

安装器已改成“先清旧，再装新，再重建设备”的顺序。

### 3.1 推荐流程

1. 移除现有的软件设备实例
2. 枚举并删除所有属于 SideDock 的旧驱动包
3. 安装当前发布包中的新驱动
4. 重新启动 `SideDock.Idd.DeviceTool.exe`

### 3.2 建议命令

```powershell
pnputil /remove-device SWD\SideDockIdd\SideDockIdd
pnputil /enum-drivers /class Display /files
pnputil /delete-driver oemXX.inf /uninstall /force
pnputil /add-driver .\SideDock.Idd.inf /install
```

说明：

- `oemXX.inf` 需要从枚举结果里按 Provider、Class、Original Name、文件路径综合判断
- `delete-driver` 前最好确认目标确实属于 `SideDock.Idd`
- 如果当前设备实例还在运行，先移除设备通常更稳

## 4. 代码层实现

“删除旧驱动包”已补到 `windows-driver/SideDock.Driver.Installer/Program.cs` 中，作为 `InstallDriver` 之前的一步。

核心流程函数：

```csharp
static void RemoveExistingDriverPackages()
{
    // 1. 枚举 Display 类驱动包
    // 2. 找到 SideDock.Idd 对应的 oemXX.inf
    // 3. 逐个执行 pnputil /delete-driver ... /uninstall /force
}
```

### 4.1 匹配规则建议

当前实现优先用这些信息识别旧包：

- `ProviderName`
- `ClassName`
- `Original Name`
- 配套 `.cat` 文件和驱动二进制文件

删除前必须同时满足 Display 类驱动包和 SideDock 精确信号，例如 `ProviderName=SideDock`、`Original Name=SideDock.Idd.inf`、`Catalog File=SideDock.Idd.cat` 或 `Driver Files` 中包含 `SideDock.Idd.dll`，避免误删别的显示驱动。

### 4.2 版本策略建议

每次发布驱动包时同步更新 `SideDock.Idd.inf` 里的 `DriverVer`，这样 Windows 的版本判断更稳定。

## 5. 验证方式

安装完成后，建议检查以下内容：

1. `pnputil /enum-devices /class Display /deviceids /drivers`
2. `pnputil /enum-drivers /class Display /files`
3. Windows 显示设置里是否出现 `SideDock Virtual Display`
4. `SideDock.Idd.DeviceTool.exe` 是否保持运行

如果重装后显示器没有出现，优先检查：

- 是否仍有旧的 `oemXX.inf`
- `DriverVer` 是否变化
- 安装器是否成功导入证书
- `DeviceTool` 是否成功启动

## 6. 当前结论

现在的“安装/修复驱动”按钮已经加入“删除旧 SideDock 驱动包”这一步，并会在安装当前包前停止旧设备工具、移除旧软件设备实例。

这样用户只需要点一次 Host App 里的按钮，安装器就能完成一次更干净、可重复的重装。后续发布驱动包时仍需要保持 `DriverVer` 随构建同步更新。
