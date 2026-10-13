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
