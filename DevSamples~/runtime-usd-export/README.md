# Runtime USD Export — Dev Test Sample

**내부 개발자 테스트 전용** 샘플이다. 이 폴더는 `DevSamples~/`(끝에 `~`) 아래에 있어 Unity가 asset으로 import하지 않고, **release 배포판에도 포함되지 않는다** — 이 dev repo를 clone한 사람만 사용한다. (`Native~`·`Documentation~`와 같은 dev-only 관례.)

## 구성
- `runtime-usd-export.usd` — Unity 런타임 exporter가 뽑은 샘플 씬 결과물
- `runtime-usd-export_textures/` — 위 usd가 참조하는 텍스처(형제 폴더, 상대경로/폴더명 유지 필수)

## 용도
Exporter/Importer 동작 검증용 레퍼런스. usdview / Isaac Sim / Omniverse 등에서 열어 export된 geometry·머티리얼·텍스처 링크를 확인한다.

## 주의
- `.usd`/텍스처는 용량이 커서 **Git LFS**로 관리된다. clone 후 필요 시 `git lfs pull`.
- 텍스처 폴더명을 바꾸면 usd 참조가 깨진다.
