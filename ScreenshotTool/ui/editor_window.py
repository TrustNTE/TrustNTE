# -*- coding: utf-8 -*-
"""编辑器: 平移/选框裁剪/画笔/填充/取色/缩放/二值化/灰度, 保存返回主界面"""
import tkinter as tk
from tkinter import messagebox
from PIL import Image, ImageDraw, ImageTk

from core import image_ops
from core.color_utils import rgb_to_hex

TOOLS = [("pan", "平移"), ("select", "选框"), ("pen", "画笔"),
         ("fill", "填充"), ("eye", "取色"), ("zoomin", "放大"), ("zoomout", "缩小")]


class EditorWindow(tk.Toplevel):
    def __init__(self, master, image, on_apply):
        super().__init__(master)
        self.title("图像编辑 (先“选框”再“执行裁剪”; 滚轮缩放)")
        self.geometry("960x680")
        self.on_apply = on_apply

        self.image = image.convert("RGB").copy()
        self.orig = self.image.copy()
        self.undo_stack = [self.image.copy()]
        self.zoom = 1.0
        self.offset = (0, 0)
        self.tool = tk.StringVar(value="pen")
        self.pen_color = (0xFF, 0x00, 0xFF)      # FF00FF, 同参考图
        self.pen_w = tk.IntVar(value=3)
        self.th = tk.IntVar(value=128)

        self._last = None
        self._drag = None
        self._rect = None
        self._img_id = None
        self._photo = None

        self._build_ui()
        self.after(60, self._refit)

    # ================= 界面 =================
    def _build_ui(self):
        bar = tk.LabelFrame(self, text="工具栏")
        bar.pack(side="left", fill="y", padx=6, pady=6)
        for val, label in TOOLS:
            tk.Radiobutton(bar, text=label, value=val, variable=self.tool,
                           indicatoron=0, width=10, relief="ridge",
                           selectcolor="#cfe3ff").pack(pady=2, padx=4)

        right = tk.Frame(self)
        right.pack(side="left", fill="both", expand=True)

        ops = tk.LabelFrame(right, text="图像操作")
        ops.pack(fill="x", padx=6, pady=(6, 2))
        tk.Button(ops, text="撤销", width=8, command=self.undo).pack(side="left", padx=2, pady=3)
        tk.Button(ops, text="复位", width=8, command=self.reset).pack(side="left", padx=2)
        tk.Button(ops, text="执行裁剪", width=9, command=self.do_crop).pack(side="left", padx=(14, 2))
        tk.Button(ops, text="二值化", width=8, command=self.do_binary).pack(side="left", padx=2)
        tk.Spinbox(ops, from_=0, to=255, textvariable=self.th, width=5).pack(side="left", padx=2)
        tk.Button(ops, text="灰度", width=8, command=self.do_gray).pack(side="left", padx=2)
        tk.Label(ops, text="线宽").pack(side="left", padx=(14, 2))
        tk.Spinbox(ops, from_=1, to=30, textvariable=self.pen_w, width=4).pack(side="left")

        cf = tk.LabelFrame(right, text="图形区域")
        cf.pack(fill="both", expand=True, padx=6, pady=4)
        self.canvas = tk.Canvas(cf, bg="#c0c0c0", highlightthickness=1,
                                highlightbackground="#888")
        self.canvas.pack(fill="both", expand=True)
        self.canvas.bind("<Button-1>", self.on_down)
        self.canvas.bind("<B1-Motion>", self.on_drag)
        self.canvas.bind("<ButtonRelease-1>", self.on_up)
        self.canvas.bind("<Motion>", self.on_move)
        self.canvas.bind("<MouseWheel>", self.on_wheel)

        bot = tk.Frame(right)
        bot.pack(fill="x", padx=6, pady=4)
        tk.Label(bot, text="RGB").pack(side="left")
        self.ent_rgb = tk.Entry(bot, width=10, justify="center")
        self.ent_rgb.pack(side="left", padx=4)
        self.ent_rgb.insert(0, "FF00FF")
        self.ent_rgb.bind("<Return>", self.on_rgb_entry)
        self.lbl_swatch = tk.Label(bot, width=3, background="#FF00FF", relief="groove")
        self.lbl_swatch.pack(side="left")
        self.lbl_zoom = tk.Label(bot, text="100%", width=8)
        self.lbl_zoom.pack(side="left", padx=10)
        tk.Button(bot, text="保存返回", width=10,
                  command=self.apply_and_close).pack(side="right", padx=4)
        tk.Button(bot, text="放弃更改", width=10, command=self.destroy).pack(side="right", padx=4)

        tk.Label(right, text="提示: 鼠标移动即取色 (取色工具点击=设为画笔色); RGB框可输入颜色回车",
                 fg="#666").pack(anchor="w", padx=8, pady=(0, 6))

    # ================= 显示 / 坐标 =================
    def _refit(self):
        self._fit_zoom()
        self.redraw()

    def _fit_zoom(self):
        cw = max(self.canvas.winfo_width(), 640)
        ch = max(self.canvas.winfo_height(), 460)
        self.zoom = max(min(cw / self.image.width, ch / self.image.height, 4.0), 0.05)
        self.offset = (0, 0)

    def redraw(self):
        dw = max(1, round(self.image.width * self.zoom))
        dh = max(1, round(self.image.height * self.zoom))
        disp = self.image if (dw, dh) == self.image.size \
            else self.image.resize((dw, dh))
        self._photo = ImageTk.PhotoImage(disp)
        self.canvas.delete("all")
        self._rect = None
        self._img_id = self.canvas.create_image(self.offset, anchor="nw",
                                                image=self._photo)
        self.lbl_zoom.config(text="%d%%" % round(self.zoom * 100))

    def canvas_to_image(self, cx, cy):
        ox, oy = self.offset
        ix, iy = int((cx - ox) / self.zoom), int((cy - oy) / self.zoom)
        if 0 <= ix < self.image.width and 0 <= iy < self.image.height:
            return ix, iy
        return None

    def _zoom_at(self, factor, cx=None, cy=None):
        old = self.zoom
        new = max(0.05, min(8.0, old * factor))
        if abs(new - old) < 1e-9:
            return
        if cx is None:
            cx, cy = self.canvas.winfo_width() // 2, self.canvas.winfo_height() // 2
        ox, oy = self.offset
        self.offset = (cx - (cx - ox) * (new / old), cy - (cy - oy) * (new / old))
        self.zoom = new
        self.redraw()

    # ================= 工具事件 =================
    def push_undo(self):
        self.undo_stack.append(self.image.copy())
        if len(self.undo_stack) > 30:
            self.undo_stack.pop(0)

    def on_down(self, e):
        t, p = self.tool.get(), self.canvas_to_image(e.x, e.y)
        if t == "pen" and p:
            self.push_undo()
            self._last = p
            self._stroke(p, p)
        elif t == "fill" and p:
            self.push_undo()
            ImageDraw.floodfill(self.image, p, self.pen_color, thresh=32)
            self.redraw()
        elif t == "eye" and p:
            self._set_pen(self.image.getpixel(p)[:3])
        elif t in ("select", "pan"):
            self._drag = (e.x, e.y, self.offset) if t == "pan" else (e.x, e.y)
        elif t == "zoomin":
            self._zoom_at(1.25)
        elif t == "zoomout":
            self._zoom_at(1 / 1.25)

    def on_drag(self, e):
        t = self.tool.get()
        if t == "pen" and self._last is not None:
            p = self.canvas_to_image(e.x, e.y)
            if p:
                self._stroke(self._last, p)
                self._last = p
        elif t == "select" and self._drag is not None:
            if self._rect:
                self.canvas.delete(self._rect)
            self._rect = self.canvas.create_rectangle(
                self._drag[0], self._drag[1], e.x, e.y,
                outline="#ff2222", dash=(4, 2))
        elif t == "pan" and self._drag is not None:
            x0, y0, (ox, oy) = self._drag
            self.offset = (ox + e.x - x0, oy + e.y - y0)
            self.canvas.coords(self._img_id, *self.offset)

    def on_up(self, _=None):
        self._last = None
        self._drag = None

    def _stroke(self, p1, p2):
        d = ImageDraw.Draw(self.image)
        w = max(1, self.pen_w.get())
        d.line([p1, p2], fill=self.pen_color, width=w)
        r = w // 2
        d.ellipse([p2[0] - r, p2[1] - r, p2[0] + r, p2[1] + r], fill=self.pen_color)
        self.redraw()

    def on_move(self, e):
        """鼠标移动即取色(同参考图提示)"""
        p = self.canvas_to_image(e.x, e.y)
        if p is None:
            return
        rgb = self.image.getpixel(p)[:3]
        self.ent_rgb.delete(0, "end")
        self.ent_rgb.insert(0, rgb_to_hex(*rgb))
        self.lbl_swatch.config(background="#" + rgb_to_hex(*rgb))
        if self.tool.get() == "eye":
            self.pen_color = rgb

    def on_wheel(self, e):
        self._zoom_at(1.15 if e.delta > 0 else 1 / 1.15, e.x, e.y)

    def on_rgb_entry(self, _=None):
        s = self.ent_rgb.get().strip().lstrip("#")
        if len(s) == 6:
            try:
                self._set_pen(tuple(int(s[i:i + 2], 16) for i in (0, 2, 4)))
            except ValueError:
                pass

    def _set_pen(self, rgb):
        self.pen_color = tuple(rgb[:3])
        hx = rgb_to_hex(*self.pen_color)
        self.ent_rgb.delete(0, "end")
        self.ent_rgb.insert(0, hx)
        self.lbl_swatch.config(background="#" + hx)

    # ================= 图像操作 =================
    def undo(self):
        if len(self.undo_stack) > 1:
            self.undo_stack.pop()
            self.image = self.undo_stack[-1].copy()
            self.redraw()

    def reset(self):
        self.image = self.orig.copy()
        self.undo_stack = [self.image.copy()]
        self._fit_zoom()
        self.redraw()

    def do_binary(self):
        self.push_undo()
        self.image = image_ops.to_binary(self.image, self.th.get())
        self.redraw()

    def do_gray(self):
        self.push_undo()
        self.image = image_ops.to_gray(self.image)
        self.redraw()

    def do_crop(self):
        if not self._rect:
            messagebox.showinfo("提示", "请先用“选框”工具拖出区域", parent=self)
            return
        c = self.canvas.coords(self._rect)
        p1 = self.canvas_to_image(c[0], c[1])
        p2 = self.canvas_to_image(c[2], c[3])
        if not p1 or not p2 or abs(p2[0] - p1[0]) < 2 or abs(p2[1] - p1[1]) < 2:
            return
        self.push_undo()
        self.image = image_ops.safe_crop(self.image, (p1[0], p1[1], p2[0], p2[1]))
        self._fit_zoom()
        self.redraw()

    def apply_and_close(self):
        try:
            self.on_apply(self.image.copy())
        finally:
            self.destroy()
