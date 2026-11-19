# 把 GitHub 最新 Release 同步到 CNB Releases
# 改编自 BetterGI (babalae) 的 cnb 同步脚本, MIT License

import argparse
import os
import sys

import requests
from tqdm import tqdm

GITHUB_REPO = "TrustNTE/TrustNTE"      # GitHub 仓库
CNB_REPO = "TrustNTE/TrustNTE"         # CNB 仓库

GITHUB_API = "https://api.github.com"
CNB_API = "https://api.cnb.cool"


def get_latest_release(github_token: str):
    headers = {"Accept": "application/vnd.github+json"}
    if github_token:
        headers["Authorization"] = f"Bearer {github_token}"
    r = requests.get(f"{GITHUB_API}/repos/{GITHUB_REPO}/releases/latest", headers=headers, timeout=30)
    r.raise_for_status()
    return r.json()


def create_cnb_release(cnb_token: str, tag_name: str, name: str, body: str):
    r = requests.post(
        f"{CNB_API}/{CNB_REPO}/-/releases",
        headers={"Authorization": f"Bearer {cnb_token}", "Content-Type": "application/json"},
        json={"tag_name": tag_name, "name": name, "body": body},
        timeout=30,
    )
    if r.status_code not in (200, 201):
        print(f"创建 CNB Release 失败: {r.status_code} {r.text}")
        sys.exit(1)
    return r.json()


def download_file(url: str, token: str, save_path: str):
    headers = {"Authorization": f"Bearer {token}"} if token else {}
    with requests.get(url, headers=headers, stream=True, timeout=60) as r:
        r.raise_for_status()
        total = int(r.headers.get("content-length", 0))
        with open(save_path, "wb") as f, tqdm(total=total, unit="B", unit_scale=True, desc=os.path.basename(save_path)) as bar:
            for chunk in r.iter_content(chunk_size=1024 * 512):
                f.write(chunk)
                bar.update(len(chunk))


def upload_file(cnb_token: str, tag_name: str, file_path: str):
    file_name = os.path.basename(file_path)
    size = os.path.getsize(file_path)
    # 1. 申请上传地址
    r = requests.post(
        f"{CNB_API}/{CNB_REPO}/-/releases/{tag_name}/upload",
        headers={"Authorization": f"Bearer {cnb_token}", "Content-Type": "application/json"},
        json={"filename": file_name, "size": size},
        timeout=30,
    )
    r.raise_for_status()
    data = r.json()
    upload_url = data.get("upload_url") or data.get("url")
    # 2. PUT 上传文件
    with open(file_path, "rb") as f:
        put = requests.put(upload_url, data=f, timeout=600)
        put.raise_for_status()
    print(f"上传完成: {file_name}")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--cnb-token", required=True)
    parser.add_argument("--github-token", default=os.environ.get("GITHUB_TOKEN", ""))
    args = parser.parse_args()

    release = get_latest_release(args.github_token)
    tag_name = release["tag_name"]
    print(f"最新 Release: {tag_name}")

    # 本地临时目录
    tmp = "release_assets"
    os.makedirs(tmp, exist_ok=True)

    for asset in release["assets"]:
        path = os.path.join(tmp, asset["name"])
        download_file(asset["browser_download_url"], args.github_token, path)
        upload_file(args.cnb_token, tag_name, path)

    print("done!")


if __name__ == "__main__":
    main()
