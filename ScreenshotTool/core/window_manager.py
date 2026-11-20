# -*- coding: utf-8 -*-
"""窗口管理: 枚举窗口 / 绑定句柄 / 后台截图"""
import ctypes

import win32con
import win32gui
import win32process
import win32ui
from PIL import Image

user32 = ctypes.windll.user32

PW_CLIENTONLY = 0x00000001
PW_RENDERFULLCONTENT = 0x00000002

_IGNORE_CLASSES = {"Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd"}


def enable_dpi_awareness():
    """进程 DPI 感知, 保证坐标/客户区尺寸为真实像素"""
    try:
        ctypes.windll.shcore.SetProcessDpiAwareness(2)
    except Exception:
        try:
            user32.SetProcessDPIAware()
        except Exception:
            pass


def enum_top_windows():
    """枚举可绑定的顶层窗口 -> [(hwnd, 标题, 类名), ...]"""
    result = []
    self_pid = ctypes.windll.kernel32.GetCurrentProcessId()

    def _cb(hwnd, _):
        if not win32gui.IsWindowVisible(hwnd):
            return
        title = win32gui.GetWindowText(hwnd).strip()
        if not title:
            return
        cls = win32gui.GetClassName(hwnd)
        if cls in _IGNORE_CLASSES:
            return
        try:
            _, pid = win32process.GetWindowThreadProcessId(hwnd)
        except Exception:
            return
        if pid == self_pid:               # 过滤自身
            return
        ex = win32gui.GetWindowLong(hwnd, win32con.GWL_EXSTYLE)
        if ex & win32con.WS_EX_TOOLWINDOW:  # 过滤工具窗口
            return
        result.append((hwnd, title, cls))

    win32gui.EnumWindows(_cb, None)
    return result


class WindowCapture:
    """绑定窗口句柄, 后台抓取客户区图像"""

    def __init__(self, hwnd=None):
        self.hwnd = hwnd

    # ---------- 绑定 ----------
    def bind(self, hwnd):
        if hwnd and not win32gui.IsWindow(hwnd):
            raise ValueError("无效的窗口句柄: 0x%08X" % hwnd)
        self.hwnd = hwnd

    def is_valid(self):
        return bool(self.hwnd) and win32gui.IsWindow(self.hwnd)

    # ---------- 客户区信息 ----------
    def client_size(self):
        l, t, r, b = win32gui.GetClientRect(self.hwnd)
        return r - l, b - t

    def client_origin(self):
        """客户区原点对应的屏幕坐标"""
        return win32gui.ClientToScreen(self.hwnd, (0, 0))

    def ensure_visible(self):
        """最小化的窗口抓不到内容, 自动还原一次"""
        if win32gui.IsIconic(self.hwnd):
            win32gui.ShowWindow(self.hwnd, win32con.SW_RESTORE)

    # ---------- 抓图 ----------
    def capture(self):
        """
        后台截图(客户区):
        1. PrintWindow(CLIENTONLY|RENDERFULLCONTENT)  -> 被遮挡也能抓
        2. PrintWindow(整窗) 再裁出客户区              -> 兼容部分特殊程序
        3. BitBlt 兜底(需要窗口未被遮挡)
        """
        if not self.is_valid():
            raise RuntimeError("尚未绑定有效窗口")
        self.ensure_visible()
        w, h = self.client_size()
        if w <= 0 or h <= 0:
            raise RuntimeError("客户区尺寸无效")

        img = self._print_window(w, h, PW_CLIENTONLY | PW_RENDERFULLCONTENT)
        if img is None:
            try:
                img = self._print_window_full()
            except Exception:
                img = None
        if img is None:
            img = self._bitblt(w, h)
        return img

    def _print_window(self, w, h, flags):
        try:
            hwnd_dc = win32gui.GetWindowDC(self.hwnd)
        except Exception:
            return None
        mfc_dc = mem_dc = bmp = None
        try:
            mfc_dc = win32ui.CreateDCFromHandle(hwnd_dc)
            mem_dc = mfc_dc.CreateCompatibleDC()
            bmp = win32ui.CreateBitmap()
            bmp.CreateCompatibleBitmap(mfc_dc, w, h)
            mem_dc.SelectObject(bmp)
            ok = user32.PrintWindow(self.hwnd, mem_dc.GetSafeHdc(), flags)
            if not ok:
                return None
            info = bmp.GetInfo()
            data = bmp.GetBitmapBits(True)
            return Image.frombuffer("RGB", (info["bmWidth"], info["bmHeight"]),
                                    data, "raw", "BGRX", 0, 1)
        except Exception:
            return None
        finally:
            try:
                if bmp is not None:
                    win32gui.DeleteObject(bmp.GetHandle())
                if mem_dc is not None:
                    mem_dc.DeleteDC()
                if mfc_dc is not None:
                    mfc_dc.DeleteDC()
                win32gui.ReleaseDC(self.hwnd, hwnd_dc)
            except Exception:
                pass

    def _print_window_full(self):
        """整窗抓图后裁出客户区(部分程序 CLIENTONLY 模式抓不到)"""
        wl, wt, wr, wb = win32gui.GetWindowRect(self.hwnd)
        img = self._print_window(wr - wl, wb - wt, PW_RENDERFULLCONTENT)
        if img is None:
            return None
        cl, ct, cr, cb = win32gui.GetClientRect(self.hwnd)
        cx, cy = self.client_origin()
        ox, oy = max(0, cx - wl), max(0, cy - wt)
        return img.crop((ox, oy, ox + (cr - cl), oy + (cb - ct)))

    def _bitblt(self, w, h):
        try:
            sx, sy = self.client_origin()
            sdc = win32gui.GetDC(0)
            mfc_dc = win32ui.CreateDCFromHandle(sdc)
            mem_dc = mfc_dc.CreateCompatibleDC()
            bmp = win32ui.CreateBitmap()
            bmp.CreateCompatibleBitmap(mfc_dc, w, h)
            mem_dc.SelectObject(bmp)
            try:
                mem_dc.BitBlt((0, 0), (w, h), mfc_dc, (sx, sy), win32con.SRCCOPY)
                info = bmp.GetInfo()
                data = bmp.GetBitmapBits(True)
                return Image.frombuffer("RGB", (info["bmWidth"], info["bmHeight"]),
                                        data, "raw", "BGRX", 0, 1)
            finally:
                win32gui.DeleteObject(bmp.GetHandle())
                mem_dc.DeleteDC()
                mfc_dc.DeleteDC()
                win32gui.ReleaseDC(0, sdc)
        except Exception as e:
            raise RuntimeError("截图失败: %s" % e)
