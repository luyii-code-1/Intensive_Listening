# 配置阿里云百炼转写

需要从录音自动生成字幕时，先配置自己的百炼 API Key。手里已有匹配的 SRT 时可以直接导入，不必配置。

## 打开 API 设置

**👉️ 左侧点「设置」，选择「制作与智能体」，再点开「API 设置」。**

<figure class="shot">
  <img src="./assets/guide/settings-api.png" alt="设置页的「制作与智能体」分类，包含 API 设置与 MCP" loading="lazy">
  <span class="shot-pin" style="--x:50%;--y:44%"><span class="shot-pin-label">API 设置</span></span>
  <figcaption>同一页下方的 MCP 开关留给智能体制作。</figcaption>
</figure>

## 创建 API Key

1. **👉️ 打开[阿里云百炼控制台](https://bailian.console.aliyun.com/)，开通服务。**
2. **👉️ 进入「API Key」页面，创建并复制 Key。**
3. **👉️ 回到应用，把 Key 粘贴到「API 设置」里，点「保存 API 配置」。**

::: tip 北京地域的默认参数
| 项目 | 值 |
| --- | --- |
| API Endpoint | `https://dashscope.aliyuncs.com` |
| Path | `/api/v1/services/aigc/multimodal-generation/generation` |
| Name | `qwen-audio-3.0-asr-flash` |
| 分段并发 | 默认 10，可设置 1–10 |
| 单段超时 | 默认 180 秒 |

使用其他地域时，先按[阿里云地域说明](https://help.aliyun.com/zh/model-studio/regions)核对接入地址与模型。
:::

## 开始转写

1. **👉️ 回到课程项目，绑定音频后在「转写」步骤点「开始转写」。**
2. **👉️ 点左下角「转写任务」查看进度。**
3. **👉️ 任务完成后点「载入字幕」。**

<figure class="shot">
  <img src="./assets/guide/queue.png" alt="转写任务列表，每个任务有载入字幕、导出 SRT 和删除按钮" loading="lazy">
  <span class="shot-pin" style="--x:12%;--y:19%"><span class="shot-pin-label">载入字幕</span></span>
  <figcaption>也可以先「导出 SRT」备份，再回到工程校对。</figcaption>
</figure>

**完成标志：** 工程里出现与录音对应的逐句字幕和时间轴。
