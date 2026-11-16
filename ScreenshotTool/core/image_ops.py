# -*- coding: utf-8 -*-
"""图像处理: 灰度 / 二值化 / 安全裁剪"""


def to_gray(img):
    return img.convert("L").convert("RGB")


def to_binary(img, threshold=128):
    bw = img.convert("L").point(lambda p: 255 if p >= threshold else 0)
    return bw.convert("RGB")


def safe_crop(img, box):
    x1, y1, x2, y2 = box
    if x1 > x2:
        x1, x2 = x2, x1
    if y1 > y2:
        y1, y2 = y2, y1
    x1 = max(0, min(int(x1), img.width - 1))
    y1 = max(0, min(int(y1), img.height - 1))
    x2 = max(x1 + 1, min(int(x2), img.width))
    y2 = max(y1 + 1, min(int(y2), img.height))
    return img.crop((x1, y1, x2, y2))
