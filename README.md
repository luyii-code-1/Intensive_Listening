# Intensive Listening 官网

产品官网静态源码。分支根目录即站点根目录，直接交给 Pages 托管即可发布。

- 站点入口 `index.html`，教程 `guide.html`
- 样式 `styles.css`，视觉语言「考卷与红笔」，与 Web 播放器同源
- `release.js` 在 `downloads/latest.json` 存在时把下载链接切到本站镜像；不存在则回退 GitHub Release 直链，无需配置
- `assets/` 品牌标识、界面标本与教程配图

配套分支：

| 分支 | 内容 | 站点 |
| --- | --- | --- |
| `website` | 本分支，产品官网 | `il.luyii.cn` |
| `web-edition` | 浏览器精听播放器（PWA） | `web.il.luyii.cn` |

设计说明见工作区 `PWAedition/DESIGN.md` 与 `PWAedition/ACCEPTANCE.md`。

## EdgeOne 同域下载代理验证

`edgeone.json` 固定构建命令为 `node scripts/build.mjs`、静态输出目录为 `dist`。边缘函数源码位于项目根目录的 `edge-functions/`，交给 EO 构建。构建不需要安装第三方依赖。

`edge-functions/downloads/[tag]/[file].js` 提供 `/downloads/v2.x.y/Intensive-Listening-2.x.y-win-x64-Setup.exe` 和对应 `osx-arm64.dmg` 路径。函数从本仓库的 GitHub Release 读取附件，跟随 GitHub 附件重定向，直接流式返回，支持 HEAD 和 Range。部分响应和错误响应使用 `no-store`；完整响应提供一小时缓存指令，实际缓存效果需在 EO 验证。

本阶段先验证下载路径。官网按钮接入及旧部署链路清理在 EO 实际下载验证通过后进行。

本地验证（Node.js 22，无需安装依赖）：

```sh
npm test
node scripts/verify-download-proxy.mjs
```

EO 发布后验证：

```sh
node scripts/verify-download-proxy.mjs https://il.luyii.cn
```

验证脚本检查 v2.1.0 双平台安装包的 HEAD、Range、完整大小与 GitHub 公布的 SHA-256。每次完整验证会传输约 278 MB。EO 实际速度、传输限制和缓存命中需以线上结果为准。
