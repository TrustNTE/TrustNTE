// ═══ 玩家血条 OCR v4:双 ROI 分离 + 总量锚定 ═══
const CUR_ROI = { x: 909, y: 1000, w: 48, h: 28 };   // 当前血量
const MAX_ROI = { x: 966, y: 1001, w: 52, h: 25 };   // 总血量(常量,只标定一次)

function digits(roi) {
    return vision.OcrRegion(roi.x, roi.y, roi.w, roi.h).replace(/[^\d]/g, "");
}

// ── 第一步:标定总血量(读5次取众数——常量不怕慢,就怕单次误认) ──
async function calibrateMax() {
    const seen = {};
    for (let i = 0; i < 5; i++) {
        const d = digits(MAX_ROI);
        if (d.length >= 4) seen[d] = (seen[d] || 0) + 1;
        await sleep(200);
    }
    let best = null, n = 0;
    for (const [k, v] of Object.entries(seen)) if (v > n) { best = +k; n = v; }
    return best;
}

const MAX = await calibrateMax();
if (!MAX) return "❌ 总血量标定失败,截一张 MAX_ROI 区域的图发我";
log.info(`总血量锚定: ${MAX} ✅`);

// ── 第二步:每帧只读当前血量,3 连采取中位数 ──
let lastValid = null;
log.info("10 秒采样,保持掉血状态");
for (let i = 1; i <= 10; i++) {
    const cand = [];
    for (let k = 0; k < 3; k++) {
        const d = digits(CUR_ROI), cur = +d;
        if (d.length >= 3 && cur > 0 && cur <= MAX) cand.push(cur);
        await sleep(120);
    }
    cand.sort((a, b) => a - b);
    if (cand.length) lastValid = cand[Math.floor(cand.length / 2)];   // 中位数
    const pct = lastValid ? Math.round(lastValid / MAX * 100) + "%" : "…";
    log.info(`#${i} → ${pct}`);
    await sleep(700);
}
return "百分比应稳定跟踪真实血量";
