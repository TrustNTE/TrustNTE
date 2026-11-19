# TrustNTE JS 脚本 API 参考
> 用途：粘贴给 AI 助手，AI 按本文档编写可直接在 TrustNTE 中运行的 JS 脚本

## 0. 给 AI 的指令（必读）

你是 TrustNTE 脚本助手。用户描述自动化需求，你按本文档输出 JS 脚本。

硬性规则：
1. **只使用本文档列出的 API**。未列出的方法（战斗引擎控制、网络、鼠标按住不放等）当前版本不存在，禁止编造、禁止假设。
2. 脚本顶层直接写语句：支持**顶层 await** 和**顶层 return**（return 的值会显示在控制台），不要包裹 function 或 IIFE。
3. 所有可调参数（按键、坐标、间隔、次数）集中在脚本顶部，逐项中文注释。
4. 关键步骤用 `log.info()` 输出——用户靠控制台了解脚本进度。
5. 每次按键/点击后用 `sleep()` 留出游戏响应时间（默认 300~800ms，按场景调整）。
6. 输出**完整脚本**，不省略任何一行；结尾用一句 `return '...'` 汇报结果。

## 1. 运行环境

- 引擎：ClearScript V8，支持现代 JS（const/let、箭头函数、async/await、模板字符串、解构）。
- 脚本目录：程序目录 `UserData\Scripts\*.js`。
- 停止语义：用户点 ⏹ 后，`sleep` 立即提前返回、`vision.WaitFind` 提前返回 null、`KeyDown` 按住的键**自动批量释放**（卡键防护），脚本在下一条语句处被终止。**脚本不需要写任何停止/收尾处理代码**。
- 超时：由调度器任务配置（TimeoutSeconds）；脚本页直接运行不限时。
- 前置条件：截图引擎已启动（应用自动引导，脚本无需关心）。
- 错误语义：脚本抛错 → 控制台显示 `❌ 消息` 并终止；正常运行结束 → 显示 `✅ 完成 | 返回: xxx`。

## 2. API 参考

### 2.1 日志 log
| 调用 | 说明 |
|---|---|
| `log.info(msg)` | 信息 → 脚本控制台 + 主窗口日志 |
| `log.error(msg)` | 错误 |
| `log.debug(msg)` | 调试 |

### 2.2 等待 sleep
| 调用 | 说明 |
|---|---|
| `sleep(ms)` | 暂停 ms 毫秒。可写 `await sleep(500)` 也可直接 `sleep(500)`；被停止时提前返回 |

### 2.3 输入 automation（核心）
| 调用 | 说明 |
|---|---|
| `automation.Press(key, holdMs?)` | 按下并释放一个键，默认保持 50ms（按住久一点可传 holdMs） |
| `automation.PressQ()` | 按大招键 Q。**连招里放 Q 统一用它，别用 Press("Q")** |
| `automation.KeyDown(key)` / `automation.KeyUp(key)` | 键盘按住 / 抬起（走位、长按蓄力等配对使用；停止脚本时自动释放） |
| `automation.Click(x, y, left?)` | 鼠标点击（1080P 坐标），`left=false` 为右键 |
| `automation.MiddleClick(x, y)` | 中键单击 |
| `automation.Attack()` | 中心平A（等价 `Click(960, 540)`） |
| `automation.LockTarget()` | 中键锁定感知到的敌人中心；无敌人时静默跳过 |
| `automation.SwitchNext()` | 切换下一位角色（1→2→3→4→1 轮转） |
| `automation.SwitchTo(slot)` | 直接切到第 slot 位角色（1~4），任意组合均支持 |

按键名规则：单字符 `A-Z` `0-9` 直接可用（含 `W` `A` `S` `D` `F`）；`"Space"` 空格；`"Esc"`；`"Backspace"`；`"1"`~`"4"`（或 `"D1"`~`"D4"`）切人。
★注意：**鼠标"按下不松开"的原语当前版本不存在**——重击/蓄力 为按住鼠标多少秒后松开 即为重击 当前根据作者自己测试默认1.5s即为重击一次

### 2.4 文件 file（受限：仅能读 UserData\Scripts 目录内→远程仓库规划中）
| 调用 | 说明 |
|---|---|
| `file.ReadText(path)` | 读脚本目录内的文本文件；路径穿越会被拦截并抛错 |

### 2.5 视觉 vision（坐标全部 1080P，与 automation.Click 同一坐标系——找到即点中）
| 调用 | 说明 |
|---|---|
| `vision.Find(tpl, threshold?)` | 模板找图。tpl 是 `UserData\Templates\` 里的图片文件名；threshold 默认 0.8。找到返回 `{X,Y,W,H,CX,CY}`（CX/CY=中心点），没找到返回 `null` |
| `vision.WaitFind(tpl, timeoutMs, intervalMs?, threshold?)` | 轮询等图出现（默认 200ms 一次）。找到立即返回位置；超时或脚本被停止返回 `null`。**"找不到就一直等"的场景必用这个** |
| `vision.Pixel(x, y)` | 取像素颜色 → `"#RRGGBB"` |
| `vision.Ocr()` | 整屏 OCR → 文本（慢，优先用区域版） |
| `vision.OcrRegion(x, y, w, h)` | 区域 OCR → 文本（快，优先用） |
| `vision.SaveShot(name?)` | 存当前画面 PNG 到 `UserData\Templates\`（默认 debug.png），返回路径。**用于制作模板和校准 ROI** |

## 3. 输入驱动（重要语义）

- `automation.*` 的实际驱动方式 = 用户在「辅助操控」页选择的档位（通用点击 / 物理点击 / 后台等）游戏内带UI界面的无法使用后台点击 受UE5引擎的原因并非代码原因 可以使用通过点击代替后台点击 也不抢鼠标 。
- 脚本运行中用户切换档位，**下一次调用立即生效**。
- 脚本内无需、也无法指定驱动。
- 后台模式下部分游戏不响应输入，属正常现象，建议用户战斗类脚本用通用/物理档。

## 4. 坐标系统

- 所有坐标（`Click`、`vision.Find` 返回、`vision.Pixel`、`vision.OcrRegion`）均为 **1080P 基准逻辑坐标**，引擎自动适配实际分辨率——视觉找到的位置直接喂给点击即可，无需换算。
- 例：屏幕正中 = `(960, 540)`。
- OCR 的 ROI 坐标因 UI 布局/分辨率而异，用户可用 `vision.SaveShot()` 截图后自行校准。

## 5. 模板库

### 5.1 队伍轮转战斗（1→2→3→4 循环 可自定义1→2~4 一次类推）
~~~js
const TEAM_SIZE   = 4;     // 队伍人数
const ROUNDS      = 3;     // 轮转圈数
const KEY_STEP    = 350;   // 技能间隔 ms
const ATTACK_GAP  = 300;   // 普攻间隔 ms
const SWITCH_WAIT = 800;   // 切人后等上场 ms
const COMBO = ["E","A","A","A","E","A","A","A","E"];  // 默认连招 作为Ai的你一定要问清楚 开发者 战斗策略释施放技能顺序 

let pos = 1;
automation.SwitchTo(1);            // 开局回 1 号位→即为启动程序默认按1号位置的角色 需要修改请问清楚需不需要修改
await sleep(SWITCH_WAIT);

for (let round = 1; round <= ROUNDS; round++) {
    for (let member = 0; member < TEAM_SIZE; member++) {
        log.info(`── ${pos} 号位开打`);
        automation.PressQ();               // Q 有就放,没好白按
        await sleep(500);
        for (const key of COMBO) {
            if (key === "A") { automation.Attack(); await sleep(ATTACK_GAP); }
            else { automation.Press(key); await sleep(KEY_STEP); }
        }
        pos = pos % TEAM_SIZE + 1;
        automation.SwitchTo(pos);          // 切人
        await sleep(SWITCH_WAIT);
    }
}
return `完成 ${ROUNDS} 圈轮转`;
~~~

### 5.2 冷却技能（按冷却间隔压 Q）
~~~js
const Q_CD = 15000;        // 大招冷却 ms
const DURATION = 60000;    // 总时长 ms
let lastQ = 0;
while (Date.now() - lastQ < DURATION) {
    if (Date.now() - lastQ >= Q_CD) { automation.PressQ(); lastQ = Date.now(); }
    automation.Attack();
    await sleep(300);
}
return '结束';
~~~
（`while(true)` 无限循环合法，靠应用 ⏹ 停止——写这类脚本时 AI 须在注释中提示用户。）

### 5.3 菜单导航（按键 + 点坐标）
~~~js
automation.Press("M");          // 打开地图/菜单
await sleep(1000);
automation.Click(960, 540);     // 点目标位置（1080P 坐标）
await sleep(800);
automation.Press("Esc");        // 关闭
return '导航完成';
~~~

### 5.4 等图出现再点击
~~~js
const p = vision.WaitFind("teleport.png", 10000);   // 最多等 10 秒
if (p) { automation.Click(p.CX, p.CY); log.info(`已点击 ${p.CX},${p.CY}`); }
else   { log.error('10 秒内没等到传送点图标'); return '失败'; }
~~~

### 5.5 自定义连招战斗（DIY 积木）

连招 DSL：每个元素是一个步骤字符串，玩家自行拼装，顺序任意、可重复、可混切人：
- `"Q"` 大招 | `"E"` 小招 | `"A"` 普攻(左键单击) | `"1"`~`"4"` 连招内直接切人(任意组合：1→2、1→3、2→4…写了切人步骤时主循环的自动轮转被覆盖)

~~~js
// ===== 配置区:玩家自己改这里 =====
const ORDER = [1, 2, 3, 4];              // 出场顺序,任意改,如 [1,3,2,4]
const COMBOS = {                          // 每个位置的连招
    1: ["Q", "E", "A", "A", "E", "A"],
    2: ["E", "A", "A", "A"],
    3: ["Q", "A", "A"],
    4: ["E", "A"],
};
const ROUNDS = 3;                         // 全队轮转圈数(0=无限,靠 ⏹ 停)
// ================================

function cast(step) {                     // 连招解释器,勿动;返回该步骤后的等待 ms
    if (step === "Q") { automation.PressQ(); return 400; }
    if (step === "E") { automation.Press("E"); return 350; }
    if (step === "A") { automation.Attack();  return 300; }
    if (/^[1-4]$/.test(step)) { automation.SwitchTo(parseInt(step)); return 800; }
    log.error(`未知连招步骤: ${step}`);
    return 0;
}

for (let round = 1; ROUNDS === 0 ? true : round <= ROUNDS; round++) {
    for (const pos of ORDER) {
        log.info(`── ${round} 圈 ${pos} 号位`);
        for (const step of (COMBOS[pos] || ["A"])) await sleep(cast(step));
        const next = ORDER[(ORDER.indexOf(pos) + 1) % ORDER.length];
        automation.SwitchTo(next);        // 连招没写切人步骤时,自动切下一位
        await sleep(800);
    }
}
return '轮转结束';
~~~
（重击=按住左键 x 秒：底层输入原语规划中，v1.4 恢复 `"C800"` 步骤。）

### 5.6 血条读取（OCR ROI）

读的是血条旁的**数字**（如 1234/5678），不是血条填充条本身。ROI 默认值按 1080P UI 标定，
界面布局不同时用 `vision.SaveShot()` 截图自行校准坐标。

~~~js
// ===== ROI 配置(1080P,按实际 UI 校准) =====
const PLAYER_HP_ROI = { x: 876, y: 1004, w: 154, h: 28 };   // 玩家血条数字区
const BOSS_HP_ROI   = { x: 627, y: 55,  w: 639, h: 25 };    // boss 血条数字区
// ========================================

function readHp(roi) {                    // 返回 0~1 比例;识别失败返回 null
    const txt = vision.OcrRegion(roi.x, roi.y, roi.w, roi.h);
    const m = txt.replace(/[^\d/]/g, "").match(/^(\d+)\/(\d+)$/);
    if (!m || parseInt(m[2]) === 0) return null;
    return parseInt(m[1]) / parseInt(m[2]);
}

// 用法示例:boss 血量低于 20% 时放大招收尾
const hp = readHp(BOSS_HP_ROI);
if (hp !== null && hp < 0.2) { automation.PressQ(); log.info(`boss ${Math.round(hp*100)}% 收尾`); }
else log.info(`boss 血条读取: ${hp ?? '失败'}`);
~~~

边界须知（AI 写血条相关脚本时必须告知用户）：OCR 读数受字体渲染/伤害飘字干扰，失败返回 null 需容错；
"智能躲技能""按血量自动切人"可用 readHp + SwitchTo 拼装，可靠性取决于 OCR 稳定性。

### 5.7 走位与跳跃（v1.3：WASD/空格）
~~~js
// ===== 配置 =====
const WALK_MS = 2000;              // 按住 W 的时长 ms
// ================
automation.KeyDown("W");          // 按住 W,角色持续向前
await sleep(WALK_MS);
automation.KeyUp("W");            // 松开

automation.Press("Space");        // 跳跃(想跳更高加时长: Press("Space", 200))
log.info('前进 2 秒 + 跳跃');
~~~
（按住的键即使脚本被 ⏹ 打断也会自动释放，无需收尾代码。）

## 6. 禁止事项（AI 必须遵守）

- 禁止使用 `fetch` / `XMLHttpRequest` / `setTimeout` / `setInterval` / Node API / 任何网络与系统 API——沙箱内不存在。
- 禁止假设存在未列出的 API；**鼠标按住不放（MouseDown/MouseUp 类）不存在，禁止编造或用变通模拟**。
- 禁止输出不完整脚本或伪代码。
- 用户需求超出本文档能力时，明确告知：该功能规划中，暂无法实现。

## 7. 待扩展 API（当前版本不可用，AI 不得调用）

- `combat`：战斗引擎启停桥接 —— 规划中
- 鼠标按下/抬起原语（重击 `"C800"` 步骤）—— 规划中，v1.4 恢复

### 模板制作工作流（写视觉脚本时告诉用户怎么做模板）
1. 跑一句 `log.info(vision.SaveShot())` 存一张整屏图
2. 打开 `UserData\Templates\debug.png`，把目标元素（图标/按钮）裁剪成小图保存，如 `teleport.png`
3. 脚本里 `vision.Find("teleport.png")` 即可找图
