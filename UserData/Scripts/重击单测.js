// ═══ HoldTest.js:重击单测 ⏹ 停止 ═══
log.info("按住左键 1.5s …");
automation.AttackHold(1500);
await sleep(1000);
log.info("按住左键 5s …");
automation.AttackHold(5000);
await sleep(1000);
log.info("按住左键 20s,期间随时 ⏹ 验证 ~50ms 抬起 …");
automation.AttackHold(20000);
return "HoldTest 完成";
