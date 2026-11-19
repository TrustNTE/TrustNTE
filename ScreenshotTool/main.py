# -*- coding: utf-8 -*-
"""截图综合工具入口
运行: python main.py
依赖: pip install pillow pywin32
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from core.window_manager import enable_dpi_awareness


def main():
    enable_dpi_awareness()          # DPI感知, 保证坐标=真实像素
    from ui.main_window import MainWindow
    MainWindow().mainloop()


if __name__ == "__main__":
    main()
