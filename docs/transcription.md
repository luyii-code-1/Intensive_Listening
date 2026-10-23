# 选择和配置转写服务

先选择转写方式：使用 Intensive Listening 云服务，或使用自己的阿里云百炼接口。已有与音频对应的 SRT 字幕时，在工程里点「导入 SRT」即可继续制作。

## 使用 Intensive Listening 云服务

1. 打开「设置 → 云服务」，启用云服务并点击「浏览器登录」。
2. 在浏览器中完成登录和授权，再回到应用，确认显示已登录。
3. 进入「设置 → 制作与智能体 → API 设置」，选择使用云服务的转写方式。
4. 查看账号积分和服务计费说明，再回到工程点击「开始转写」。任务进度在「转写任务」中查看。

转写提示等待中断时，先检查网络，再在原任务上点「重试」。原任务可能仍保留在云端，先确认其状态再决定是否重新建立任务。

## 使用自己的百炼 API Key

### 打开「设置 → 制作与智能体」

左侧点「设置」，在分类中选择「制作与智能体」，再点开「API 设置」。同一页下方的 MCP 开关留给智能体制作。

![设置页的「制作与智能体」分类，包含 API 设置与 MCP](https://il.luyii.cn/assets/guide/settings-api.png)

「API 设置」是转写配置入口，右侧「配置帮助」会打开本页。

### 创建 API Key 并填写

打开[阿里云百炼控制台](https://bailian.console.aliyun.com/)，开通服务并创建 Key。回到应用，在「API 设置」里粘贴 Key；使用北京地域时其余参数保持默认。

| 项目 | 说明 |
| --- | --- |
| Endpoint | `https://dashscope.aliyuncs.com` |
| Path | `/api/v1/services/aigc/multimodal-generation/generation` |
| Name | `qwen-audio-3.0-asr-flash` |
| Key | 粘贴自己的百炼 API Key |
| 分段并发 | 默认 10，可设置为 1–10 |
| 单段超时 | 默认 180 秒 |

点击「保存 API 配置」。使用其他地域时，先按[阿里云地域说明](https://help.aliyun.com/zh/model-studio/regions)核对接入地址与模型。

**完成标志：** 工程里可以点「开始转写」，不再提示 API 未配置。

## 在任务中心查看转写

点左下角「转写任务」打开任务列表。每个任务完成后可以「载入字幕」「导出 SRT」或删除；转写期间可以继续做别的事。

![转写任务列表，每个任务有载入字幕、导出 SRT 和删除按钮](https://il.luyii.cn/assets/guide/queue.png)

任务按时间排列，完成后显示「载入字幕」。

**完成标志：** 任务显示「已完成」，工程中出现逐句字幕与时间轴。
