# -*- coding: utf-8 -*-
""" 式冻结取色器:
画面冻结 -> 移动瞄准(十字线+放大镜, 实时RGB/HSV)
单击 = 取单点颜色 ; 按住拖拽 = 框选区域(Shift+方向键微调, Enter确认) ; Esc/右键 = 取消
"""
import tkinter as tk

import win32api
import win32con
import win32gui
import win32ui
from PIL import Image, ImageTk

from core.color_utils import pixel_info

# 关键修复: win32con 里没有 CAPTUREBLT, 手动定义
ROP_SRCCOPY = 0x00CC0020
ROP_CAPTUREBLT = 0x40000000

MAG_SRC = 15          # 放大镜取样半径(源像素)
MAG_ZOOM = 14         # 放大倍数
PANEL_H = 232         # 信息面板高度
DRAG_THRESHOLD = 3    # 超过此位移视为拖拽框选

_NEAREST = getattr(Image, "Resampling", Image).NEAREST


def capture_virtual_screen():
    """BitBlt 抓取整个虚拟屏幕(支持多显示器), 返回 (PIL.Image, (vx, vy))"""
    vx = win32api.GetSystemMetrics(win32con.SM_XVIRTUALSCREEN)
    vy = win32api.GetSystemMetrics(win32con.SM_YVIRTUALSCREEN)
    vw = win32api.GetSystemMetrics(win32con.SM_CXVIRTUALSCREEN)
    vh = win32api.GetSystemMetrics(win32con.SM_CYVIRTUALSCREEN)
    if vw <= 0 or vh <= 0:
        vx, vy = 0, 0
        vw = win32api.GetSystemMetrics(win32con.SM_CXSCREEN)
        vh = win32api.GetSystemMetrics(win32con.SM_CYSCREEN)

    sdc = win32gui.GetDC(0)
    mfc_dc = win32ui.CreateDCFromHandle(sdc)
    mem_dc = mfc_dc.CreateCompatibleDC()
    bmp = win32ui.CreateBitmap()
    bmp.CreateCompatibleBitmap(mfc_dc, vw, vh)
    mem_dc.SelectObject(bmp)
    try:
        try:
            mem_dc.BitBlt((0, 0), (vw, vh), mfc_dc, (vx, vy),
                          ROP_SRCCOPY | ROP_CAPTUREBLT)
        except Exception:
            mem_dc.BitBlt((0, 0), (vw, vh), mfc_dc, (vx, vy), ROP_SRCCOPY)
        info = bmp.GetInfo()
        data = bmp.GetBitmapBits(True)
        img = Image.frombuffer("RGB", (info["bmWidth"], info["bmHeight"]),
                               data, "raw", "BGRX", 0, 1)
    finally:
        try:
            win32gui.DeleteObject(bmp.GetHandle())
            mem_dc.DeleteDC()
            mfc_dc.DeleteDC()
            win32gui.ReleaseDC(0, sdc)
        except Exception:
            pass
    return img, (vx, vy)


class ScreenPicker(tk.Toplevel):
    """
    on_pick(client_xy|None, screen_xy, info)              单击取点回调
    on_rect(screen_rect, snapshot_img, snapshot_origin)   Enter确认框选回调
    """

    def __init__(self, master, cap, on_pick, on_rect=None):
        # 先抓屏! 失败直接抛异常, 此时还没创建窗口, 不会留下孤儿窗
        self.screen_img, (self.vx, self.vy) = capture_virtual_screen()

        super().__init__(master)
        self.cap = cap
        self.on_pick = on_pick
        self.on_rect = on_rect

        self._done = False
        self._press = None        # 鼠标按下点
        self._start = None        # 框选起点
        self._end = None          # 框选终点
        self._rect_done = False   # 拖拽结束, 等待 Enter
        self._mouse = (0, 0)
        self._overlay_ids = []
        self._mag_photo = None

        self.vw, self.vh = self.screen_img.size

        # 无边框置顶全屏窗, 铺静态快照 => "冻结"效果
        self.overrideredirect(True)
        self.geometry("%dx%d+%d+%d" % (self.vw, self.vh, self.vx, self.vy))
        self.attributes("-topmost", True)
        self.configure(bg="black")
        self.canvas = tk.Canvas(self, bg="black", highlightthickness=0,
                                cursor="crosshair")
        self.canvas.pack(fill="both", expand=True)

        self._bg = ImageTk.PhotoImage(self.screen_img)
        self.canvas.create_image(0, 0, anchor="nw", image=self._bg)
        self.canvas.create_text(10, 8, anchor="nw", fill="#ffff00",
                                font=("宋体", 11, "bold"),
                                text="画面已冻结 | 单击=取色 | 拖拽=框选 | "
                                     "Shift+方向键微调 | Enter确认 | Esc取消")

        # 鼠标
        self.canvas.bind("<Button-1>", self._on_press)
        self.canvas.bind("<B1-Motion>", self._on_drag)
        self.canvas.bind("<ButtonRelease-1>", self._on_release)
        self.canvas.bind("<Motion>", self._on_move)
        # 键盘
        for seq in ("<Return>", "<KP_Enter>"):
            self.bind(seq, self._on_enter)
            self.canvas.bind(seq, self._on_enter)
        for key in ("Left", "Right", "Up", "Down"):
            self.bind("<Shift-%s>" % key, self._on_nudge)
            self.bind("<%s>" % key, self._on_nudge)
        self.bind("<Escape>", self._cancel)
        self.bind("<Button-3>", self._cancel)

        self.lift()
        self.focus_force()
        self.update_idletasks()
        try:
            self.grab_set_global()
        except tk.TclError:
            try:
                self.grab_set()
            except tk.TclError:
                pass
        self._overlay(*self._mouse)

    # ---------- 坐标工具 ----------
    def _to_client(self, sx, sy):
        try:
            if self.cap is not None and self.cap.is_valid():
                return win32gui.ScreenToClient(self.cap.hwnd, (sx, sy))
        except Exception:
            pass
        return None

    def _cur_rect(self):
        if not (self._start and self._end):
            return None
        x1, x2 = sorted((self._start[0], self._end[0]))
        y1, y2 = sorted((self._start[1], self._end[1]))
        return (x1, y1, x2, y2)

    # ---------- 取消 / 取点 / 确认 ----------
    def _cancel(self, _=None):
        if not self._done:
            self._done = True
            self.destroy()

    def _pick_point(self, x, y):
        if self._done:
            return
        if not (0 <= x < self.vw and 0 <= y < self.vh):
            return
        self._done = True
        rgb = self.screen_img.getpixel((x, y))[:3]     # 读快照, 不受覆盖物影响
        info = pixel_info(*rgb)
        sx, sy = self.vx + x, self.vy + y
        client = self._to_client(sx, sy)
        self.destroy()                                  # 恢复画面
        try:
            self.on_pick(client, (sx, sy), info)
        except Exception:
            pass

    def _on_enter(self, _=None):
        r = self._cur_rect()
        if self._done or not r or not self._rect_done:
            return
        w, h = r[2] - r[0], r[3] - r[1]
        if w < 2 or h < 2:
            return
        self._done = True
        rect = (self.vx + r[0], self.vy + r[1],
                self.vx + r[2], self.vy + r[3])
        self.destroy()
        if self.on_rect:
            try:
                self.on_rect(rect, self.screen_img, (self.vx, self.vy))
            except Exception:
                pass

    # ---------- 鼠标事件 ----------
    def _on_press(self, e):
        self._press = (e.x, e.y)
        self._start = (e.x, e.y)
        self._end = (e.x, e.y)
        self._rect_done = False
        self._mouse = (e.x, e.y)
        self._overlay(e.x, e.y)

    def _on_drag(self, e):
        if self._press is None:
            return
        self._end = (e.x, e.y)
        self._mouse = (e.x, e.y)
        self._overlay(e.x, e.y)

    def _on_release(self, e):
        if self._press is None:
            return
        moved = abs(e.x - self._press[0]) + abs(e.y - self._press[1])
        self._press = None
        if moved < DRAG_THRESHOLD:
            # 单击 => 取单点颜色
            self._start = self._end = None
            self._rect_done = False
            self._pick_point(e.x, e.y)
            return
        # 拖拽结束 => 保留选框, 等待 Enter / Shift 微调
        self._rect_done = True
        self._mouse = (e.x, e.y)
        self._overlay(e.x, e.y)

    def _on_move(self, e):
        self._mouse = (e.x, e.y)
        self._overlay(e.x, e.y)

    def _on_nudge(self, e):
        """Shift+方向键(或方向键): 微调选框终点 => 调整大小"""
        if self._done or not (self._start and self._end):
            return
        dx, dy = {"Left": (-1, 0), "Right": (1, 0),
                  "Up": (0, -1), "Down": (0, 1)}[e.keysym]
        ex = max(self._start[0], min(self.vw - 1, self._end[0] + dx))
        ey = max(self._start[1], min(self.vh - 1, self._end[1] + dy))
        self._end = (ex, ey)
        self._rect_done = True
        self._overlay(ex, ey)

    # ---------- 覆盖层绘制: 十字线 + 选框 + 放大镜 + 信息面板 ----------
    def _overlay(self, x, y):
        for i in self._overlay_ids:
            self.canvas.delete(i)
        self._overlay_ids = []
        if self._done:
            return

        half, zoom = MAG_SRC, MAG_ZOOM
        size = 2 * half + 1
        disp = size * zoom

        # --- 十字线 ---
        self._overlay_ids += [
            self.canvas.create_line(0, y, self.vw, y, fill="#00e000"),
            self.canvas.create_line(x, 0, x, self.vh, fill="#00e000"),
        ]

        # --- 选框 ---
        r = self._cur_rect()
        if r and (r[2] - r[0] >= 1 or r[3] - r[1] >= 1):
            self._overlay_ids.append(self.canvas.create_rectangle(
                r, outline="#00e000", width=2))

        # --- 放大镜位置(靠边翻转) ---
        mx = x + 18 if x + 18 + disp < self.vw - 4 else x - 18 - disp
        my = y + 18 if y + 18 + disp + PANEL_H < self.vh - 4 else y - 18 - disp - PANEL_H
        mx = max(4, min(mx, self.vw - disp - 4))
        my = max(4, min(my, self.vh - disp - PANEL_H - 4))

        # --- 放大镜图像 ---
        region = Image.new("RGB", (size, size), (30, 30, 30))
        x0, y0 = x - half, y - half
        sx0, sy0 = max(0, x0), max(0, y0)
        sx1, sy1 = min(self.vw, x0 + size), min(self.vh, y0 + size)
        if sx1 > sx0 and sy1 > sy0:
            region.paste(self.screen_img.crop((sx0, sy0, sx1, sy1)),
                         (sx0 - x0, sy0 - y0))
        photo = ImageTk.PhotoImage(region.resize((disp, disp), _NEAREST))
        self._mag_photo = photo
        self._overlay_ids += [
            self.canvas.create_image(mx, my, anchor="nw", image=photo),
            self.canvas.create_rectangle(mx, my, mx + disp, my + disp,
                                         outline="#404040"),
        ]
        # 放大镜中心十字(红色) + 中心像素框(蓝色)
        c = half * zoom
        cxm, cym = mx + c + zoom // 2, my + c + zoom // 2
        self._overlay_ids += [
            self.canvas.create_line(cxm - 28, cym, cxm + 28, cym, fill="#ff2020"),
            self.canvas.create_line(cxm, cym - 28, cxm, cym + 28, fill="#ff2020"),
            self.canvas.create_rectangle(mx + c, my + c,
                                         mx + c + zoom, my + c + zoom,
                                         outline="#2030ff", width=2),
        ]

        # --- 信息面板 (起点/终点/大小/颜色/相对坐标, 同 ) ---
        rgb = self.screen_img.getpixel((x, y))[:3]
        info = pixel_info(*rgb)
        sx, sy = self.vx + x, self.vy + y
        client = self._to_client(sx, sy)
        if r:
            p1 = (self.vx + r[0], self.vy + r[1])
            p2 = (self.vx + r[2], self.vy + r[3])
            line_start = "起点: %d,%d" % p1
            line_end = "终点: %d,%d" % p2
            line_size = "大小: %d,%d" % (r[2] - r[0], r[3] - r[1])
        else:
            line_start = "起点: %d,%d" % (sx, sy)
            line_end = "终点: %d,%d" % (sx, sy)
            line_size = "大小: 0,0"
        rel = "%d,%d" % client if client else "%d,%d" % (sx, sy)

        ty = my + disp + 6
        pw = disp
        self._overlay_ids.append(self.canvas.create_rectangle(
            mx, ty, mx + pw, ty + PANEL_H, fill="#ffffe1", outline="#808080"))
        data_lines = [line_start, line_end, line_size,
                      "颜色: %s" % info["rgb_hex"].lower(),
                      "相对坐标: %s" % rel]
        yy = ty + 8
        for s in data_lines:
            self._overlay_ids.append(self.canvas.create_text(
                mx + 10, yy, anchor="nw", text=s, font=("宋体", 12, "bold")))
            yy += 24
        yy += 2
        self._overlay_ids.append(self.canvas.create_line(
            mx + 8, yy, mx + pw - 8, yy, fill="#808080"))
        yy += 6
        for s in ("拖拽: 框选区域(方向键微调)",
                  "Enter: 确认框选",
                  "Esc/右键: 取消"):
            self._overlay_ids.append(self.canvas.create_text(
                mx + 10, yy, anchor="nw", text=s,
                font=("宋体", 10), fill="#555555"))
            yy += 20
