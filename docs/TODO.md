# 后续可做

v1.0.0 的硬件控制全部走厂商 MICommonInterface WMI（`root\WMI`），不依赖 WinRing0；
裸 EC 端口在本机不可达，结论见 [EC_REGISTERS.md](EC_REGISTERS.md)。

## 功能

- [ ] 电池限充（本机 WMI/EC 未暴露对应 command，需其他机型验证）
- [ ] 多语言界面（当前仅简体中文）
- [ ] 自动更新
- [ ] 更多机型的 capabilities 适配（当前只实测 MECHREVO 蛟龙16 Pro / MRID6-23）

## 已知限制

- 固件把单核睿频上限锁在约 4.2 GHz，APERF/MPERF 实测确认，软件无法突破。
- 独显 TGP 没有可用的写入入口，读取与频率控制可用，功耗墙不可调。
- 显卡模式切换（独显直连等）需要重启后生效，这是 MUX 的硬件行为。
