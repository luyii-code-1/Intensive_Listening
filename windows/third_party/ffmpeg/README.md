# FFmpeg 运行时依赖

- 版本：FFmpeg 9.0.1 essentials，Windows x64
- 二进制包：https://github.com/GyanD/codexffmpeg/releases/tag/9.0.1
- 对应源码：https://github.com/FFmpeg/FFmpeg/tree/n9.0.1
- 压缩包 SHA-256：`fec81ae03971d9dd4be3ebe02e263bd2ec1d789483f931bdba5f5715e65da2e9`
- 可执行文件 SHA-256：`72a489eccd008c2ec2c0a5856c5c75bc3d8bbfa90166c4566865c246445e6aa3`
- 用途：音频解码、静音检测、上传 ASR 前转为 16 kHz 单声道 PCM WAV
- 许可证：见同目录下的 `LICENSE`

`ffmpeg.exe` 是构建依赖，不纳入版本控制。请从上述二进制包取得文件，核对校验值后放置到：

```text
windows/third_party/ffmpeg/ffmpeg.exe
```

Windows Release 构建会将该文件复制到应用输出目录。
