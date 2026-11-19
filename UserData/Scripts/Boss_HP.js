// ═══ boss 血量生产版:绝对阈值 + 中位数滤波(特效免疫) ═══
const BAR = { x: 633, y: 62, w: 623, h: 7 };
const GAP = 40;
const LOW = 0.30;                        // 血条占比 < 30% 视为残血

async function readBar() {               // 连采3次取中值,滤掉技能特效尖刺
    const s = [];
    for (let i = 0; i < 3; i++) {
        s.push(vision.WarmRatio(BAR.x, BAR.y, BAR.w, BAR.h, GAP));
        await sleep(120);
    }
    return s.sort((a, b) => a - b)[1];
}

while (true) {                           // 无限循环,靠 ⏹ 停
    automation.Attack();
    await sleep(300);
    const v = await readBar();
    if (v < LOW) { log.info(`boss 残血 ${(v*100).toFixed(0)}%,放大招`); automation.PressQ(); await sleep(500); }
    else log.info(`boss 血条 ${(v*100).toFixed(0)}%`);
}
