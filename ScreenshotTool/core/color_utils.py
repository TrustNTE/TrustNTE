# -*- coding: utf-8 -*-
"""颜色工具: RGB/HSV/灰度转换 (对齐  HSV 颜色格式 H.S.V, 各 0-255)"""
import colorsys


def rgb_to_hsv(r, g, b):
    h, s, v = colorsys.rgb_to_hsv(r / 255.0, g / 255.0, b / 255.0)
    return round(h * 255), round(s * 255), round(v * 255)


def rgb_to_gray(r, g, b):
    """灰度: Y = 0.299R + 0.587G + 0.114B"""
    return round(0.299 * r + 0.587 * g + 0.114 * b)


def rgb_to_hex(r, g, b):
    return "%02X%02X%02X" % (r, g, b)


def pixel_info(r, g, b):
    h, s, v = rgb_to_hsv(r, g, b)
    return {
        "rgb": (r, g, b),
        "rgb_hex": rgb_to_hex(r, g, b),
        "hsv": (h, s, v),
        "hsv_str": "%d.%d.%d" % (h, s, v),
        "gray": rgb_to_gray(r, g, b),
    }
