### 本服务端自建单在 RIoT 被取消后的同车重建（control-server#318 来源一）。
#
# 单车、合成车载端。只改一处：重建前的延迟由产品默认 30 秒调成 8 秒，省机时；延迟本身的判据
#（差一秒不建、到点就建）在 L1 OwnOrderRebuildTests 里按拨钟测。
@{
    OwnOrderRebuildDelay = '00:00:08'
}
