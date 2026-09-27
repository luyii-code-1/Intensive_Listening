# Alibaba Cloud ARMS RUM PC SDK (Windows x64)

Source: https://help.aliyun.com/zh/arms/user-experience-monitoring/access-pc-platform-application-1

Official archive: https://rum-sdk.oss-cn-hangzhou.aliyuncs.com/native/AlibabaCloud_RUM_Windows.zip

Pinned archive version: `0.4.4`

- Archive SHA-256: `bfa05c0718a57eb7e94c9494499bd3c84305a3cc39b76d6a9096667553928994`
- `bin/x86_64/Release/alibabacloud_rum.dll` SHA-256: `33cec949309f8025be35ff19d7ae7f7bfc0f59a9ff0bef3e05c40460f0bf6e8d`

Run `scripts/prepare_windows_arms_sdk.ps1` on Windows to stage the DLL. The DLL is
ignored by Git. Alibaba Cloud support confirmed the DLL may be distributed
with the application; its notice is recorded in `THIRD_PARTY_NOTICES.md`.
The SDK remains proprietary and is not covered by this project's GPL license.
