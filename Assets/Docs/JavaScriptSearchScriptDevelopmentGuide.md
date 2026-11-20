# JS 脚本开发指南

TrustNTE 内嵌 V8 引擎（与 Chrome 同内核），脚本保存时自动做语法检查，语法错误直接拒绝保存。

## 脚本放哪、怎么跑

- 脚本目录：TrustNTE\UserData\Scripts（工具条「📂 打开目录」直达）
- 「JS 脚本」页写完点「▶ 运行」立即执行；「调度器」可把脚本串成组连续跑
- 脚本是一次性执行：需要持续工作就写循环（见示例 3）
- 「⏹ 停止」和调度器「跳过/停止」会立即掐断脚本，循环里保持 await sleep() 节奏即可安全退出

## 可用 API

| API | 说明 |
|---|---|
| log.info(msg) / log.error(msg) / log.debug(msg) | 打日志（进主窗口日志 + 脚本页控制台） |
| await sleep(ms) | 等待毫秒。必须用 await，可被停止按钮打断 |
| automation.Press("M") | 按键（走全局输入路由，支持物理/后台/欺骗三种驱动） |
| automation.Click(x, y) | 点击（1080P 逻辑坐标，自动适配分辨率） |
| automation.PressQ() / automation.Attack() | 战斗快捷操作 |
| automation.LockTarget() / automation.SwitchNext() | 锁定目标 / 切换下一位角色 |
| file | 文件沙盒：只能读写脚本目录内的文件，禁止绝对路径（防路径穿越） |

所有输入都经由项目统一输入路由（IInputRouter），停止任务时自动抬起所有按住的键，不会卡键。

## 示例 1：Hello

    log.info('脚本启动');
    await sleep(500);
    log.info('结束');
    return 'ok';

## 示例 2：循环按 M（每 2 秒一次，共 10 次）

    for (let i = 1; i <= 10; i++) {
        automation.Press("M");
        log.info(`第 ${i}/10 次打开地图`);
        await sleep(2000);
    }
    return 'done';

## 示例 3：持续采集（直到被手动停止）

    log.info('开始持续采集，点停止按钮结束');
    while (true) {
        automation.Press("F");
        await sleep(1200);
    }

## 调度器脚本 vs 战斗策略脚本

| | 调度器脚本（本页） | 战斗策略脚本（实时触发页） |
|---|---|---|
| 运行方式 | 手动 / 调度器整组执行 | 战斗引擎接敌后自动轮转 |
| 敌人感知快照 | 无 | 由战斗引擎每帧刷新 |
| 典型用途 | 日常跑图、领取、重复操作 | 连招（配合队伍轮转） |

## 注意事项

1. 每次循环务必 await sleep(...)，死循环空转会吃满 CPU
2. 坐标写 1080P 逻辑坐标（960,540 = 屏幕正中），运行时自动缩放
3. return 一个字符串，运行结果显示在控制台（不写就显示 {}，正常现象）
4. 保存即语法检查，带行号的报错直接弹在控制台
