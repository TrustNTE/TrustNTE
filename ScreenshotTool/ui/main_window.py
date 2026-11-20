# -*- coding: utf-8 -*-
"""主界面: 绑定窗口 / 后台截图 / 框选范围 / 冻结取色(单击取点+拖拽框选) / 10组取色 / 编辑"""
import re
import tkinter as tk
from tkinter import ttk, filedialog, messagebox

import win32gui

from core import image_ops
from core.color_utils import pixel_info, rgb_to_hex
from core.window_manager import WindowCapture, enum_top_windows
from ui.screen_picker import ScreenPicker

MAX_PICKS = 10
COLS = ("coord", "rgb", "hsv", "gray", "bias", "on")


def make_swatch(rgb, size=16):
    from PIL import Image as _I
    from PIL import ImageTk
    return ImageTk.PhotoImage(_I.new("RGB", (size, size), rgb))


class MainWindow(tk.Tk):
    def __init__(self):
        super().__init__()
        self.title("截图综合工具 (后台截图/冻结取色/编辑)")
        self.geometry("980x780")
        self.minsize(900, 700)

        self.cap = WindowCapture()
        self.window_map = {}
        self.img_full = None        # 整幅客户区截图
        self.img_src = None         # 范围裁剪后的取色数据源
        self.range_box = None       # (x1,y1,x2,y2) 客户区坐标
        self.zoom = 1.0
        self.offset = (0, 0)

        self.mode = tk.StringVar(value="pick")       # pick=点击取色 / range=框选范围
        self.preview = tk.StringVar(value="raw")     # raw/gray/binary
        self.gray_th = tk.IntVar(value=128)

        self.pick_rows = [{"x": None, "y": None, "enabled": False,
                           "info": None, "bias_hex": "000000"}
                          for _ in range(MAX_PICKS)]
        self._swatches = [None] * MAX_PICKS
        self._photo = None
        self._drag = None
        self._rect_id = None
        self._after_id = None
        self._picker = None
        self._copy_after = None

        self._build_ui()
        self._init_rows()
        self.refresh_windows()

    # ================= 界面 =================
    def _build_ui(self):
        top = tk.Frame(self)
        top.pack(fill="x", padx=8, pady=(6, 2))
        for text, cmd in (("刷 新", self.refresh_windows),
                          ("取 色", self.do_pick_freeze),
                          ("编 辑", self.do_edit),
                          ("保 存", self.do_save),
                          ("截 图", self.do_capture)):
            tk.Button(top, text=text, width=10, command=cmd).pack(side="right", padx=3)

        show = tk.Frame(self)
        show.pack(fill="both", expand=True, padx=8, pady=2)
        self.canvas = tk.Canvas(show, bg="black", highlightthickness=1,
                                highlightbackground="#999")
        self.canvas.pack(side="left", fill="both", expand=True)
        self.status = tk.Label(show, text="", width=22, anchor="nw", fg="#0055cc",
                               font=("宋体", 10))
        self.status.pack(side="right", anchor="n", padx=4)

        self.canvas.bind("<Button-1>", self.on_canvas_down)
        self.canvas.bind("<B1-Motion>", self.on_canvas_drag)
        self.canvas.bind("<ButtonRelease-1>", self.on_canvas_up)
        self.canvas.bind("<Motion>", self.on_canvas_move)
        self.canvas.bind("<Configure>", self.on_resize)

        fw = tk.LabelFrame(self, text="选择窗口")
        fw.pack(fill="x", padx=8, pady=3)
        self.cmb = ttk.Combobox(fw, state="readonly")
        self.cmb.pack(side="left", fill="x", expand=True, padx=4, pady=3)
        self.cmb.bind("<<ComboboxSelected>>", self.on_window_selected)
        self.lbl_size = tk.Label(fw, text="客户区: -", width=16)
        self.lbl_size.pack(side="left", padx=6)

        fr = tk.LabelFrame(self, text="选择范围 (客户区坐标, 回车应用)")
        fr.pack(fill="x", padx=8, pady=3)
        self.ent_range = tk.Entry(fr, width=28)
        self.ent_range.insert(0, "全客户区")
        self.ent_range.pack(side="left", padx=4, pady=3)
        self.ent_range.bind("<Return>", self.on_range_entry)
        tk.Label(fr, text="鼠标模式:").pack(side="left", padx=(16, 0))
        tk.Radiobutton(fr, text="点击取色", variable=self.mode,
                       value="pick").pack(side="left")
        tk.Radiobutton(fr, text="框选范围", variable=self.mode,
                       value="range").pack(side="left")
        tk.Button(fr, text="清除范围", command=self.clear_range).pack(side="left", padx=8)

        fg = tk.LabelFrame(self, text="灰度颜色 (二值化阈值)")
        fg.pack(fill="x", padx=8, pady=3)
        tk.Scale(fg, from_=0, to=255, orient="horizontal", variable=self.gray_th,
                 length=340, command=self.on_slider).pack(side="left", padx=4)
        for txt, val in (("原图", "raw"), ("灰度", "gray"), ("二值化", "binary")):
            tk.Radiobutton(fg, text=txt, variable=self.preview, value=val,
                           command=self.redraw).pack(side="left", padx=6)

        ft = tk.LabelFrame(self, text="取色区 (10组; 点色块→全屏冻结→单击取色/拖拽框选Enter确认; 点数值格=复制; 双击偏色改容差)")
        ft.pack(fill="both", expand=True, padx=8, pady=(3, 8))
        self.tree = ttk.Treeview(ft, columns=COLS, height=MAX_PICKS,
                                 show="tree headings")
        self.tree.heading("#0", text="颜色")
        self.tree.column("#0", width=100, minwidth=60, stretch=False)
        for cid, txt, w in (("coord", "坐标", 110), ("rgb", "RGB", 90),
                            ("hsv", "HSV", 110), ("gray", "灰度", 60),
                            ("bias", "偏色", 80), ("on", "启用", 50)):
            self.tree.heading(cid, text=txt)
            self.tree.column(cid, width=w, minwidth=40, stretch=False, anchor="center")
        ttk.Style(self).configure("Pick.Treeview", rowheight=22)
        self.tree.configure(style="Pick.Treeview")
        self.tree.pack(side="left", fill="both", expand=True)
        ttk.Scrollbar(ft, orient="vertical",
                      command=self.tree.yview).pack(side="left", fill="y")
        self.tree.bind("<Button-1>", self.on_tree_click)
        self.tree.bind("<Double-Button-1>", self.on_tree_dclick)

        side = tk.Frame(ft)
        side.pack(side="left", fill="y", padx=6)
        tk.Button(side, text="一键清除", width=12, command=self.clear_picks).pack(pady=4)
        tk.Button(side, text="复制颜色", width=12, command=self.copy_picks).pack(pady=4)
        tk.Button(side, text="清除当前行", width=12, command=self.clear_row).pack(pady=4)
        self.lbl_copy = tk.Label(side, text="", fg="#008000", font=("宋体", 9))
        self.lbl_copy.pack(pady=4)

    def _init_rows(self):
        black = make_swatch((0, 0, 0))
        for i in range(MAX_PICKS):
            self._swatches[i] = black
            self.tree.insert("", "end", iid="row%d" % i, image=black,
                             text=str(i + 1),
                             values=("", "000000", "0.0.0", "0", "000000", "□"))

    # ================= 窗口绑定 =================
    def refresh_windows(self):
        items, self.window_map = [], {}
        for hwnd, title, cls in enum_top_windows():
            label = "0x%08X  %s  [%s]" % (hwnd, title[:36], cls[:20])
            items.append(label)
            self.window_map[label] = hwnd
        self.cmb["values"] = items

    def on_window_selected(self, _=None):
        hwnd = self.window_map.get(self.cmb.get())
        if not hwnd:
            return
        try:
            self.cap.bind(hwnd)
            w, h = self.cap.client_size()
            self.lbl_size.config(text="客户区: %dx%d" % (w, h))
        except Exception as e:
            messagebox.showerror("绑定失败", str(e), parent=self)

    # ================= 截图 / 显示 =================
    def do_capture(self):
        if not self.cap.is_valid():
            messagebox.showwarning("提示", "请先在“选择窗口”绑定窗口", parent=self)
            return
        try:
            self.img_full = self.cap.capture()
        except Exception as e:
            messagebox.showerror("截图失败", str(e), parent=self)
            return
        self._apply_range()

    def _apply_range(self):
        if self.img_full is None:
            return
        self.img_src = image_ops.safe_crop(self.img_full, self.range_box) \
            if self.range_box else self.img_full.copy()
        self.redraw()

    def redraw(self, *_):
        if self.img_src is None:
            return
        img = self.img_src
        if self.preview.get() == "gray":
            img = image_ops.to_gray(img)
        elif self.preview.get() == "binary":
            img = image_ops.to_binary(img, self.gray_th.get())
        cw = max(self.canvas.winfo_width(), 200)
        ch = max(self.canvas.winfo_height(), 120)
        self.zoom = min(cw / img.width, ch / img.height) or 1.0
        dw, dh = max(1, round(img.width * self.zoom)), max(1, round(img.height * self.zoom))
        from PIL import ImageTk
        self._photo = ImageTk.PhotoImage(img.resize((dw, dh)))
        self.canvas.delete("all")
        self._rect_id = None
        self.canvas.create_image(0, 0, anchor="nw", image=self._photo)
        self.offset = (0, 0)

    def on_slider(self, _=None):
        if self.preview.get() == "binary":
            self.redraw()

    def on_resize(self, _=None):
        if self.img_src is None:
            return
        if self._after_id:
            self.after_cancel(self._after_id)
        self._after_id = self.after(150, self.redraw)

    # ================= 坐标换算(客户区坐标) =================
    def canvas_to_image(self, cx, cy):
        if self.img_src is None:
            return None
        ox, oy = self.offset
        ix, iy = int((cx - ox) / self.zoom), int((cy - oy) / self.zoom)
        if 0 <= ix < self.img_src.width and 0 <= iy < self.img_src.height:
            return ix, iy
        return None

    def _client_xy(self, ix, iy):
        if self.range_box:
            return ix + self.range_box[0], iy + self.range_box[1]
        return ix, iy

    # ================= 画布鼠标: 取色 / 框选 =================
    def on_canvas_down(self, e):
        p = self.canvas_to_image(e.x, e.y)
        if p is None:
            return
        if self.mode.get() == "range":
            self._drag = (e.x, e.y)
            if self._rect_id:
                self.canvas.delete(self._rect_id)
                self._rect_id = None
        else:
            self.pick_at(*p)

    def on_canvas_drag(self, e):
        if self.mode.get() != "range" or self._drag is None:
            return
        if self._rect_id:
            self.canvas.delete(self._rect_id)
        self._rect_id = self.canvas.create_rectangle(
            self._drag[0], self._drag[1], e.x, e.y, outline="#ff3333")

    def on_canvas_up(self, e):
        if self.mode.get() != "range" or self._drag is None:
            return
        x1, y1 = self._drag
        self._drag = None
        if self._rect_id:
            self.canvas.delete(self._rect_id)
            self._rect_id = None
        p1 = self.canvas_to_image(min(x1, e.x), min(y1, e.y))
        p2 = self.canvas_to_image(max(x1, e.x), max(y1, e.y))
        if not p1 or not p2 or abs(p2[0] - p1[0]) < 3 or abs(p2[1] - p1[1]) < 3:
            return
        ox, oy = self._client_xy(0, 0)
        self.range_box = (p1[0] + ox, p1[1] + oy, p2[0] + ox, p2[1] + oy)
        self.ent_range.delete(0, "end")
        self.ent_range.insert(0, "(%d,%d,%d,%d)" % self.range_box)
        self._apply_range()

    def on_canvas_move(self, e):
        p = self.canvas_to_image(e.x, e.y)
        if p is None:
            self.status.config(text="")
            return
        rgb = self.img_src.getpixel(p)[:3]
        sx, sy = self._client_xy(*p)
        self.status.config(text="客户区坐标: (%d, %d)\n颜色: %s"
                                % (sx, sy, rgb_to_hex(*rgb)))

    # ================= 取色表 =================
    def pick_at(self, ix, iy):
        """画布点击取色(后台截图数据源), 填入下一个空行"""
        r, g, b = self.img_src.getpixel((ix, iy))[:3]
        info = pixel_info(r, g, b)
        idx = next((i for i, row in enumerate(self.pick_rows)
                    if row["x"] is None), MAX_PICKS - 1)
        self.set_pick(idx, self._client_xy(ix, iy), info)

    def set_pick(self, idx, xy, info, note=""):
        row = self.pick_rows[idx]
        row.update(x=xy[0], y=xy[1], enabled=True, info=info, bias_hex="000000")
        sw = make_swatch(info["rgb"])
        self._swatches[idx] = sw
        self.tree.item("row%d" % idx, image=sw,
                       values=("(%d,%d)%s" % (xy[0], xy[1], note),
                               info["rgb_hex"], info["hsv_str"],
                               str(info["gray"]), "000000", "√"))

    def on_tree_click(self, event):
        region = self.tree.identify("region", event.x, event.y)
        if region not in ("tree", "cell"):
            return
        col = self.tree.identify_column(event.x)
        iid = self.tree.identify_row(event.y)
        if not iid.startswith("row"):
            return
        idx = int(iid[3:])
        row = self.pick_rows[idx]

        # 点击颜色块 => 冻结全屏取色, 写入本行
        if region == "tree":
            self.start_pick(idx)
            return "break"

        # 启用复选框
        if col == "#6":
            if row["x"] is None:
                return "break"
            row["enabled"] = not row["enabled"]
            vals = list(self.tree.item(iid, "values"))
            vals[5] = "√" if row["enabled"] else "□"
            self.tree.item(iid, values=vals)
            return "break"

        # 坐标格: 空行=启动取色; 有值=复制坐标
        if col == "#1":
            if row["x"] is None:
                self.start_pick(idx)
            else:
                self._flash_copy("%d,%d" % (row["x"], row["y"]))
            return "break"

        if row["x"] is None:
            return "break"
        info = row["info"]
        # 点数值格 => 复制
        if col == "#2":
            self._flash_copy(info["rgb_hex"].lower())
        elif col == "#3":
            self._flash_copy(info["hsv_str"])
        elif col == "#4":
            self._flash_copy(str(info["gray"]))
        elif col == "#5":
            self._flash_copy(row["bias_hex"].lower())
        return "break"

    def _flash_copy(self, text):
        self.clipboard_clear()
        self.clipboard_append(text)
        self.lbl_copy.config(text="已复制: %s" % text)
        if self._copy_after:
            self.after_cancel(self._copy_after)
        self._copy_after = self.after(1500, lambda: self.lbl_copy.config(text=""))

    def on_tree_dclick(self, event):
        """双击偏色单元格 -> 行内编辑容差( 格式 RRGGBB)"""
        col = self.tree.identify_column(event.x)
        iid = self.tree.identify_row(event.y)
        if col != "#5" or not iid.startswith("row"):
            return
        idx = int(iid[3:])
        if self.pick_rows[idx]["x"] is None:
            return
        box = self.tree.bbox(iid, "bias")
        if not box:
            return
        x, y, w, h = box
        entry = tk.Entry(self.tree, justify="center")
        entry.place(x=x, y=y, width=w, height=h)
        entry.insert(0, self.tree.set(iid, "bias"))
        entry.focus_set()

        def save(_=None):
            v = entry.get().strip().lower()
            if len(v) == 6:
                try:
                    int(v, 16)
                    self.tree.set(iid, "bias", v.upper())
                    self.pick_rows[idx]["bias_hex"] = v.upper()
                except ValueError:
                    pass
            entry.destroy()

        entry.bind("<Return>", save)
        entry.bind("<FocusOut>", save)

    # ================= 冻结取色 ( /QQ截图式) =================
    def do_pick_freeze(self):
        """工具栏“取色”: 冻结全屏, 取到的颜色填入下一个空行"""
        idx = next((i for i, r in enumerate(self.pick_rows) if r["x"] is None), 0)
        self.start_pick(idx)

    def _picker_alive(self):
        p = self._picker
        if p is None:
            return False
        try:
            return bool(p.winfo_exists())
        except tk.TclError:
            return False

    def start_pick(self, idx):
        """冻结全屏 -> 单击=取点写入第idx行; 拖拽+Enter=设为范围并重新截图"""
        if self._picker_alive():
            return

        def _picked(client_xy, screen_xy, info):
            self._picker = None
            if client_xy is not None:
                xy, note = client_xy, ""        # 已绑定窗口 -> 客户区坐标
            else:
                xy, note = screen_xy, "(屏)"    # 未绑定 -> 屏幕坐标
            self.set_pick(idx, xy, info, note)

        def _rect(screen_rect, snapshot, origin):
            self._picker = None
            self._on_rect_picked(screen_rect, snapshot, origin)

        try:
            self._picker = ScreenPicker(self, self.cap, _picked, _rect)
        except Exception as e:
            self._picker = None
            messagebox.showerror("取色器启动失败", str(e), parent=self)

    def _on_rect_picked(self, screen_rect, snapshot, origin):
        """冻结界面拖框 + Enter 确认 => 设为范围并重新后台截图"""
        try:
            if self.cap.is_valid():
                hwnd = self.cap.hwnd
                x1, y1 = win32gui.ScreenToClient(hwnd, (screen_rect[0], screen_rect[1]))
                x2, y2 = win32gui.ScreenToClient(hwnd, (screen_rect[2], screen_rect[3]))
                self.range_box = (min(x1, x2), min(y1, y2), max(x1, x2), max(y1, y2))
                self.ent_range.delete(0, "end")
                self.ent_range.insert(0, "(%d,%d,%d,%d)" % self.range_box)
                self.do_capture()               # PrintWindow 后台重抓并按范围裁剪
            else:
                # 未绑定窗口: 直接用冻结快照裁剪显示(坐标为屏幕坐标)
                ox, oy = origin
                self.img_full = snapshot.copy()
                self.range_box = screen_rect
                self.img_src = image_ops.safe_crop(
                    snapshot, (screen_rect[0] - ox, screen_rect[1] - oy,
                               screen_rect[2] - ox, screen_rect[3] - oy))
                self.ent_range.delete(0, "end")
                self.ent_range.insert(0, "屏幕(%d,%d,%d,%d)" % screen_rect)
                self.redraw()
        except Exception as e:
            messagebox.showerror("框选失败", str(e), parent=self)

    # ================= 清除 / 复制 =================
    def _reset_row(self, idx):
        self.pick_rows[idx] = {"x": None, "y": None, "enabled": False,
                               "info": None, "bias_hex": "000000"}
        black = make_swatch((0, 0, 0))
        self._swatches[idx] = black
        self.tree.item("row%d" % idx, image=black,
                       values=("", "000000", "0.0.0", "0", "000000", "□"))

    def clear_row(self):
        iid = self.tree.focus()
        if iid and iid.startswith("row"):
            self._reset_row(int(iid[3:]))

    def clear_picks(self):
        for i in range(MAX_PICKS):
            self._reset_row(i)

    def copy_picks(self):
        """复制为 多点找色格式: x,y,color-偏色|x,y,color-偏色|..."""
        parts, lines = [], []
        for i, row in enumerate(self.pick_rows):
            if row["x"] is None or not row["enabled"]:
                continue
            info = row["info"]
            parts.append("%d,%d,%s-%s" % (row["x"], row["y"],
                                          info["rgb_hex"].lower(),
                                          row["bias_hex"].lower()))
            lines.append("%d: (%d,%d) RGB=%s HSV=%s 灰度=%d 偏色=%s"
                         % (i + 1, row["x"], row["y"], info["rgb_hex"],
                            info["hsv_str"], info["gray"], row["bias_hex"]))
        if not parts:
            messagebox.showinfo("提示", "没有已启用的取色点", parent=self)
            return
        text = "\n".join(lines) + "\n\n 格式:\n" + "|".join(parts)
        self.clipboard_clear()
        self.clipboard_append(text)
        messagebox.showinfo("已复制到剪贴板", text, parent=self)

    # ================= 范围 / 保存 / 编辑 =================
    def clear_range(self):
        self.range_box = None
        self.ent_range.delete(0, "end")
        self.ent_range.insert(0, "全客户区")
        if self.img_full is not None:
            self.img_src = self.img_full.copy()
            self.redraw()

    def on_range_entry(self, _=None):
        s = self.ent_range.get().strip()
        s = s.lstrip("屏幕").strip("()（）")
        parts = re.split(r"[,，\s]+", s)
        if len(parts) != 4:
            messagebox.showwarning("格式", "格式: (x1,y1,x2,y2)", parent=self)
            return
        try:
            self.range_box = tuple(int(float(v)) for v in parts)
        except ValueError:
            messagebox.showwarning("格式", "坐标必须是数字", parent=self)
            return
        if self.img_full is not None:
            self._apply_range()

    def do_save(self):
        if self.img_src is None:
            messagebox.showwarning("提示", "还没有截图", parent=self)
            return
        path = filedialog.asksaveasfilename(
            defaultextension=".png", parent=self,
            filetypes=[("PNG", "*.png"), ("BMP", "*.bmp"), ("JPEG", "*.jpg")])
        if path:
            self.img_src.save(path)

    def do_edit(self):
        if self.img_src is None:
            messagebox.showwarning("提示", "请先截图再编辑", parent=self)
            return
        from ui.editor_window import EditorWindow
        EditorWindow(self, self.img_src, on_apply=self._on_edited)

    def _on_edited(self, img):
        self.img_full = img.copy()
        self.range_box = None
        self.ent_range.delete(0, "end")
        self.ent_range.insert(0, "全客户区")
        self.img_src = img
        self.redraw()
