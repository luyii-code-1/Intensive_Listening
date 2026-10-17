# Intensive Listening 官网

产品官网静态源码。分支根目录即站点根目录，直接交给 Pages 托管即可发布。

- 站点入口 `index.html`，教程 `guide.html`
- 样式 `styles.css`，视觉语言「考卷与红笔」，与 Web 播放器同源
- `release.js` 按 `downloads/latest.json`（发版 CI 写入）切换下载按钮与版本显示，无需手工改版本
- 下载按钮默认走境内加速节点（见下），节点不可用时回退 GitHub Release 直链
- `assets/` 品牌标识、界面标本与教程配图

## 版本号与发版联动

版本号只有一个来源：`downloads/latest.json` 的 `tag`。发版后由 `student_app` 侧的 `.github/workflows/release-metadata.yml` 写入，随后 `website` 分支的 EdgeOne 自动构建重新部署站点。

数据流：

```text
GitHub Release published
  └─ student_app/.github/workflows/release-metadata.yml
     （必须放在该仓库的默认分支 main 上，release 事件只运行默认分支的工作流）
       ├─ 校验 tag 是最新稳定版 v2.x，取 Release 元数据
       ├─ node scripts/publish-release.mjs release.json .
       │    ├─ 写 downloads/latest.json（附件名/size/sha256/Release 来源）
       │    └─ 重写 release.js 的 DEFAULT_TAG
       └─ npm test && npm run build 后 push 到 website 分支
            └─ EdgeOne 自动构建
                 └─ node scripts/build.mjs 用 tag 替换 index.html/guide.html 的 __VERSION__
```

因此改版本号只需一处：发一次 Release，其余自动跟随。

- **`__VERSION__` 占位符**：写在 `index.html` / `guide.html` 源码里的版本位置，构建时替换为不带 `v` 的版本号。`tests/publish-release.test.mjs` 与 CI 都会检查产物里没有残留占位符。
- **`DEFAULT_TAG`**：`release.js` 里的兜底版本号，仅当 `latest.json` 缺失或不可用时使用。由 CI 同步重写，正常情况无需手工维护。
- 若 Release 不是当前最新稳定版（回填旧版），工作流会跳过，避免 `release.js` 的「最新版」语义被破坏。

## Web 播放器（web-edition）版本

Web 播放器是**独立于桌面版的版本系列**（桌面版 `2.x.y`，播放器 `1.x.y+N`），页面上的「当前版本」也由 CI 同步，不再写死——此前它长期停在 `1.1.0`，而线上实际已是 `1.5.0+16`。

- **唯一来源**：`web-edition` 分支 `sw.js` 的 `CACHE_NAME = 'il-pwa-<版本>'`。改它才会让浏览器丢掉旧资源，所以它必须与显示值同批改动。
- **`web.json`**：官网侧的中间文件，记录 `{"webPlayer": "1.5.0+16"}`，由 `scripts/sync-web-version.mjs` 从 `sw.js` 生成，`build.mjs` 读它替换 `__WEB_VERSION__`。取不到时页面显示「未知」，而不是一个可能错误的旧版本号。
- **构建号递增**：`scripts/bump-web-version.mjs` 只递增 `+N`，不动 `1.x.y` 主版本——主版本由人决定。发版 CI 会调用它并推送 `web-edition`，触发该站自动重建。

版本号一共出现在五处，bump 脚本会一次改全（漏改任一都会错配）：

| 位置 | 内容 |
| --- | --- |
| `sw.js` | `CACHE_NAME = 'il-pwa-1.5.0+17'` |
| `index.html` | `<span class="version-text">` 与 `title="版本: …"` |
| `index.html` | `<span class="commit-text">`（见下：语义为「产出该版本的构建所基于的提交」） |
| `js/app.js` | `app_version: '1.5.0+17'` |

**为什么必须同批改**：`web-edition` 的 `sw.js` 把 `PRECACHE_RESOURCES` 列为预缓存，取用是 **cache-first**（`caches.match(request)` 命中即返回），旧缓存只在 `activate` 里按 `CACHE_NAME` 清理。所以产物变了而 `CACHE_NAME` 没变，**用户会一直拿旧副本**。构建号递增的触发条件也因此对齐预缓存清单（见 `web-edition` 分支的 `bump-version.yml`），而不是对齐「像代码的文件」。

**`commit-text` 的语义**：它写的是「产出该版本的构建所**基于**的提交」——即被 bump 掉的那次产物改动，**不是** bump 提交自身（那只是 `chore(release)` 空转，不引入任何产物变化）。等价判据：**`commit-text` == bump 提交的父提交**。

历次 `chore(release): Bump version to …` 提交全部符合（样本为实际值，已逐一核对）：

| 版本 | bump 提交 | `commit-text` | 父提交 |
| --- | --- | --- | --- |
| `1.0.1+8` | `274b776` | `6d0f5ac` | `6d0f5ac` ✅ |
| `1.0.1+9` | `25f9062`→`64b1338` | `274b776` / `25f9062` | 同上 ✅ |
| `1.1.0+10` | `9049c8a` | `64b1338` | `64b1338` ✅ |
| `1.2.1+12` | `4167c8b` | `winui3` | `9236223` ❌ **占位值，非准则** |

历史上的 bump 提交信息也把父提交写在括号里：`Bump version to 1.0.1+9 (25f9062)`，括号里正是父提交。

**注意两种不符合的情形，都不是准则**：

- `4167c8b`（`1.2.1+12`）起 `commit-text` 退化为手写的 `winui3` 占位，一直继承到 `1.5.0+16`。原因是当时取自身提交号不便。
- `1.5.0+16` 不是一次 bump 提交，而是 `fc311f4` 这个功能提交**自己**写了版本号，属「无 bump 提交」的近似。

这两种手工情形今后不会再出现——版本串统一由 CI 写 `$GITHUB_SHA`。而 CI 用 `$GITHUB_SHA` 成立的前提是 **bump 提交紧随产物提交、中间没有别人的提交插进来**。工作流在推送前会 `git pull --rebase`，若 rebase 期间有他人提交插入，`$GITHUB_SHA` 就不再是 bump 的父提交，语义会静默漂移；这一点已写进工作流注释。

## 下载按钮的境内加速

配套分支：

| 分支 | 内容 | 站点 |
| --- | --- | --- |
| `website` | 本分支，产品官网 | `il.luyii.cn` |
| `web-edition` | 浏览器精听播放器（PWA） | `web.il.luyii.cn` |

设计说明见工作区 `PWAedition/DESIGN.md` 与 `PWAedition/ACCEPTANCE.md`。

## 下载按钮的境内加速

`release.js` 按 `downloads/latest.json` 里的 `assets.*.source` 构造下载地址，并逐个探测 `MIRRORS` 中的加速节点：对候选地址发 `HEAD`，返回 2xx 才采用，全部失败则回退 GitHub 直链。因此**加速节点只影响下载速度，不影响安装包来源**——最终取到的仍是同一份 Release 附件。

节点收录条件：就同一个 Release 附件回传 `Access-Control-Allow-Origin`（否则浏览器 `HEAD` 探测必然失败，节点永远不会被选中）。当前顺序为 `gh-proxy.org` → `v4.gh-proxy.org` → `gh-proxy.com`；`ghproxy.net` 因不回传 CORS 头而未收录。

2026-10-17 本机 4 MB Range 实测（单机数据，不代表用户端）：

| 节点 | 速度 |
| --- | --- |
| `gh-proxy.org` | 113–282 KB/s，3/3 完成 |
| `gh-proxy.com` | 13–41 KB/s，多次超时 |
| `v4.gh-proxy.org` | 25–28 KB/s，3/3 超时 |
| `ghproxy.net` | 9.7 KB/s，超时；且无 CORS 头 |
| GitHub 直链（对照） | 27 KB/s，超时 |

2026-10-17 验证：走 `gh-proxy.org` 完整下载 Windows 包 154540989 字节，**SHA-256 与 GitHub Release 公布值一致**，确认加速节点回传的确实是原包（耗时 852 秒，181 KB/s）。该次验证的元数据随后作为 `downloads/latest.json` 的初值提交，因此线上不再依赖硬编码版本号。

`tests/download-links.test.mjs` 覆盖节点选择、逐个回退、全失败回退直链、元数据缺失/损坏时退回 `DEFAULT_TAG`，以及元数据被篡改成非 Release 域名时不动按钮。

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

线上 HEAD 响应实测由 EO 返回 `Content-Length: 0`，因此该头不能用于确定附件大小；总大小通过 Range 响应的 `Content-Range` 和完整传输校验确认。验证脚本会单独记录 HEAD 长度。

2026-10-15 验证结果：本地构建和四项代理测试通过；线上双平台 Range 请求均返回正确的 206 与 1024 字节。Windows 完整下载 154540989 字节，耗时约 290 秒，SHA-256 与 GitHub Release 公布值一致。macOS 完整下载在 360 秒时超时，已接收 96362496 字节，完整哈希尚未验证。以上速度来自执行环境，不能代表用户端速度或缓存加速收益。正式下载按钮和旧服务器部署链路暂保留，待下载体验确认后接入。
