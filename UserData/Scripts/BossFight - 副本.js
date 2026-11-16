
// ═══ boss 生产版:挂机打 boss,残血放大招 ═══
const BOSS_BAR = { x: 633, y: 62, w: 623, h: 7 };
const WARM_GAP  = 40;
const BOSS_LOW  = 0.30;

async function readBossBar() {                       // 3 连取中值,滤技能特效尖刺
    const s = [];
    for (let i = 0; i < 3; i++) {
        s.push(vision.WarmRatio(BOSS_BAR.x, BOSS_BAR.y, BOSS_BAR.w, BOSS_BAR.h, WARM_GAP));
        await sleep(120);
    }
    return s.sort((a, b) => a - b)[1];
}

let lowStreak = 0;
while (true) {
    automation.Attack();
    await sleep(300);
    const v = await readBossBar();
    if (v < BOSS_LOW) lowStreak++; else lowStreak = 0;   // 连续 2 次才触发
    if (lowStreak >= 2) {
        log.info(`boss 残血 ${(v * 100).toFixed(0)}%,放大招`);
        automation.PressQ();
        lowStreak = 0;
    }
}
