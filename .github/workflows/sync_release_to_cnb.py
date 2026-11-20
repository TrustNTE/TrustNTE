# 把 GitHub 最新 Release 同步到 CNB Releases
# 修复版: 对齐 CNB 官方上传流程(BetterGI 原版 cnb_release.py 三步走)
#   1) 先确保 CNB Release 存在(官方规则: tag 无 release 则不能传附件)
#   2) 凭 release_id 申请预签名上传地址
#   3) PUT 文件后, 如返回 verify_url 则确认落盘
import argparse
import os
import sys
import requests
from tqdm import tqdm

GITHUB_REPO = "TrustNTE/TrustNTE"   # GitHub 仓库
CNB_REPO = "TrustNTE/TrustNTE"      # CNB 仓库(须已初始化, 至少一个提交)
GITHUB_API = "https://api.github.com"
CNB_API = "https://api.cnb.cool"


def get_latest_release(github_token: str):
    headers = {"Accept": "application/vnd.github+json"}
    if github_token:
        headers["Authorization"] = f"Bearer {github_token}"
    r = requests.get(f"{GITHUB_API}/repos/{GITHUB_REPO}/releases/latest",
                     headers=headers, timeout=30)
    r.raise_for_status()
    return r.json()


def cnb_headers(cnb_token: str):
    return {
        "Accept": "application/json",
        "Authorization": f"Bearer {cnb_token}",
        "Content-Type": "application/json",
    }


def find_cnb_release(cnb_token: str, tag_name: str):
    """列出 CNB Release, 按 tag 匹配; 找不到返回 None"""
    r = requests.get(f"{CNB_API}/{CNB_REPO}/-/releases",
                     headers=cnb_headers(cnb_token), timeout=30)
    if not r.ok:
        print(f"列出 CNB Release: HTTP {r.status_code} {r.text[:300]}")
        return None
    releases = r.json()
    if isinstance(releases, dict):
        releases = releases.get("data") or releases.get("items") or []
    for rel in releases:
        if rel.get("tag_name") == tag_name:
            print(f"CNB Release 已存在: tag={tag_name} id={rel.get('id')}")
            return rel
    return None


def ensure_cnb_tag(cnb_token: str, tag_name: str, branch: str = "main"):
    """CNB 建 Release 不会自动建 tag, 必须先确保 tag 存在"""
    r = requests.post(
        f"{CNB_API}/{CNB_REPO}/-/git/tags",
        headers=cnb_headers(cnb_token),
        json={"name": tag_name, "message": f"sync tag {tag_name} from GitHub", "target": branch},
        timeout=30,
    )
    if r.ok:
        print(f"CNB tag 就绪: {tag_name}")
    else:
        print(f"CNB tag 响应: HTTP {r.status_code} {r.text[:200]} (已存在则忽略)")


def get_or_create_cnb_release(cnb_token: str, tag_name: str, name: str, body: str):
    """确保 CNB 上存在该 tag 的 Release 并返回(带 id)。先补 tag, 再建/复用 Release"""
    ensure_cnb_tag(cnb_token, tag_name)
    rel = find_cnb_release(cnb_token, tag_name)
    if rel:
        return rel

    r = requests.post(
        f"{CNB_API}/{CNB_REPO}/-/releases",
        headers=cnb_headers(cnb_token),
        json={"tag_name": tag_name, "name": name, "body": body},
        timeout=30,
    )
    print(f"创建 CNB Release: HTTP {r.status_code}")
    if r.ok:
        rel = r.json()
        print(f"CNB Release 创建成功: id={rel.get('id')} tag={rel.get('tag_name')}")
        return rel

    print(f"创建 CNB Release 失败: {r.status_code} {r.text[:500]}")
    rel = find_cnb_release(cnb_token, tag_name)  # 可能"已存在"类报错但实际已建好
    if rel:
        return rel
    sys.exit(1)


def upload_file(cnb_token: str, release_id, file_path: str):
    """官方三步: 申请上传地址 -> PUT -> 确认。端点修复: 用 release_id, 不再用不存在的 /{tag}/upload"""
    file_name = os.path.basename(file_path)
    size = os.path.getsize(file_path)
    h = cnb_headers(cnb_token)

    # 1) 凭 release_id 申请预签名上传地址(与 BetterGI 原版端点、参数一字不差)
    r = requests.post(
        f"{CNB_API}/{CNB_REPO}/-/releases/{release_id}/asset-upload-url",
        headers=h,
        json={"asset_name": file_name, "overwrite": True, "size": size},
        timeout=30,
    )
    if not r.ok:
        print(f"申请上传地址失败: HTTP {r.status_code} {r.text[:500]}")
        sys.exit(1)
    info = r.json()
    upload_url = info.get("upload_url")
    if not upload_url:
        print(f"响应中没有 upload_url: {str(info)[:500]}")
        sys.exit(1)
    print(f"已获取上传地址: {file_name} ({size} 字节)")

    # 2) PUT 文件
    with open(file_path, "rb") as f:
        put = requests.put(upload_url, headers={"Authorization": f"Bearer {cnb_token}"},
                           data=f, timeout=600)
    if not put.ok:
        print(f"上传失败: HTTP {put.status_code} {put.text[:500]}")
        sys.exit(1)
    print(f"上传完成: {file_name}")

    # 3) 如返回 verify_url 则确认
    verify_url = info.get("verify_url")
    if verify_url:
        v = requests.post(verify_url, headers=h, timeout=60)
        print(f"确认上传: HTTP {v.status_code}")
        if not v.ok:
            print(f"确认失败: {v.text[:300]}")
            sys.exit(1)
    print(f"✅ {file_name} 同步完成")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--cnb-token", required=True)
    parser.add_argument("--github-token", default=os.environ.get("GITHUB_TOKEN", ""))
    args = parser.parse_args()

    release = get_latest_release(args.github_token)
    tag_name = release["tag_name"]
    print(f"最新 Release: {tag_name}")

    rel = get_or_create_cnb_release(
        cnb_token=args.cnb_token, tag_name=tag_name,
        name=release.get("name") or tag_name, body=release.get("body") or "")
    release_id = rel.get("id")
    if release_id is None:
        print(f"CNB Release 响应中没有 id: {str(rel)[:300]}")
        sys.exit(1)

    tmp = "release_assets"
    os.makedirs(tmp, exist_ok=True)
    for asset in release["assets"]:
        path = os.path.join(tmp, asset["name"])
        download_file(asset["browser_download_url"], args.github_token, path)
        upload_file(args.cnb_token, release_id, path)

    print("done!")


if __name__ == "__main__":
    main()
