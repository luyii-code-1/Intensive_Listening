# 安装器清除集控状态

Windows 安装器支持独立清除模式：

```powershell
.\Intensive-Listening-<版本>-win-x64-Setup.exe --restoremdn
```

该模式在初始化阶段执行后退出，不进入安装向导，不覆盖应用，不导入安装器旁的组织配置或预设。运行前需完全退出应用，包括托盘驻留进程。

默认处理启动安装器的原用户的 `%LOCALAPPDATA%\Intensive Listening\data`，不扫描其他用户。自定义目录可追加 `/MDNDATA="D:\ILData"`。

清除采用可回滚事务：文件被占用时恢复原状态，解除占用后可重试；中断的事务在下次运行清除工具时恢复。

清除 `il_mdn_config.yaml`、`mdn/state.bin`、`cloud.bin`、`events.bin`、`update-receipts.bin` 及组织崩溃回执标记。保留个人设置、课库、工程、分发课程、设备 UUID 和安装标识。

从受保护缓存读取并验证现有策略。`LockManagement=true`、缓存无法解密/解析、已配置组织但缺少策略缓存时拒绝清除。通过原用户运行临时工具，使其可以读取该用户的 DPAPI 缓存；不进行在线策略查询。

结果以安装器提示和 `/LOG` 日志为准。由于独立模式在 InitializeSetup 阶段结束，Inno Setup 进程的退出码表示安装未执行，不应以安装成功退出码判断清除结果。静默模式只写日志。

发布流程将独立的自包含 `IL.MdnRestore.exe` 打包到安装器中，清除无需依赖已安装应用的版本。普通安装流程保持原有行为。
