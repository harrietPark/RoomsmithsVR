# Roomsmiths 웹 수령 서비스

로컬 검증은 프로젝트 루트에서 `node WebLab/server.mjs`로 실행하고 `http://localhost:5177/collect/index.html`을 연다. Unity Editor는 기본적으로 같은 주소를 사용한다. 다른 주소는 Editor를 시작하기 전에 `ROOMSMITHS_COLLECTION_ORIGIN`에 지정한다.

## Cloudflare Workers Free와 Supabase Free 배포

1. Supabase의 **SQL Editor → New query**에서 `WebLab/roomsmiths-supabase.sql`을 실행한다. 작품 메타데이터와 비공개 파일 저장소를 설정한다.
2. 프로젝트 루트에서 `npm exec --yes --package=wrangler@4.162.0 -- wrangler login`으로 Cloudflare에 로그인하고, `npm exec --yes --package=wrangler@4.162.0 -- wrangler deploy --config WebLab/wrangler.jsonc`로 수령 사이트와 API를 배포한다.
3. Supabase의 **Connect**에서 Project URL을 확인한다. Cloudflare의 **roomsmiths → Settings → Variables and Secrets**에서 `SUPABASE_URL`과 `SUPABASE_SECRET_KEY`를 추가한다. `SUPABASE_SECRET_KEY`는 타입을 **Secret**으로 설정하고 Supabase의 **Settings → API Keys → Secret keys**에 있는 `sb_secret_…` 값을 저장한다. 배포나 앱 소스에 키를 기록하지 않는다.
4. Worker 주소에서 샘플이 표시되는지 확인한 뒤 앱에서 테스트 작품을 전송한다. Fixer의 안내와 노트북에 표시된 코드로 브라우저에서 작품과 파일을 확인한다.

공개 서비스는 GLB·FBX·OBJ ZIP·STL·3MF와 제품 사진을 받는다. 앱이 현재 모델에서 각 형식을 직접 생성한다. 전송 상한은 한 작품당 32 MiB다. Worker가 만료된 파일을 시간별로 정리한다.

Quest 빌드에는 배포가 완료된 HTTPS 원점을 `Assets/Resources/RoomsmithsCollectionOrigin.txt`에 지정한다. 파일 내용은 원점 URL 하나이며 공유 비밀은 없다. 추측 불가능한 기기 자격값은 기기에서 생성하고 PlayerPrefs에 저장한다. 작품 전송이 성공하면 같은 기기에는 만료 전까지 같은 8자리 코드가 돌아온다. 코드를 아는 브라우저는 해당 작품 파일을 받을 수 있으므로 사용자에게 코드 노출 범위를 안내한다. 첫 발급 후 7일이 지나면 노트북에서 다시 전송해 새 코드를 받는다.

로컬 Node 수령 서버에서는 `ROOMSMITHS_BLENDER`에 Blender 실행 파일을 지정하면 GLB에서 FBX와 OBJ ZIP을 추가 생성한다. 유저 파일을 처리하는 변환 과정은 서비스 전용의 권한이 제한된 계정 또는 격리된 컨테이너에서 실행한다. 공개 사이트의 샘플에는 이미 변환한 FBX와 OBJ ZIP이 들어 있다.

사용자 정보나 방 영상 전체를 전송하지 않는다. 버튼을 직접 선택한 작품의 모델·프린트 파일과 제품 전용 사진, 제목·치수·제작일을 보낸다.

## 무료 공개 주소 (2026-11-14)

수령 주소는 https://roomsmiths.pages.dev/ 이다. 유료 도메인과 플랜 변경 없이 Pages 기본 서브도메인을 사용한다. 기존 Workers 주소도 유지한다. Pages의 정적 화면은 `public/collect`에서 준비하며 `/api/collect/*`만 service binding `COLLECTION`으로 기존 `roomsmiths` Worker에 전달한다. 인증·수령 코드·파일·만료 정리는 기존 백엔드 한 곳을 공유한다. 앱도 새 원점을 사용한다.

화면 업데이트 후 두 공개 주소를 함께 갱신한다:

```powershell
node WebLab/pages/prepare.mjs
npm exec --yes --package=wrangler@4.162.0 -- wrangler pages deploy --cwd WebLab/pages --project-name roomsmiths --branch main
npm exec --yes --package=wrangler@4.162.0 -- wrangler deploy --config WebLab/wrangler.jsonc
```

Pages 배포 출력은 `WebLab/pages/dist`에 생성한다. 이 디렉터리에는 수령 화면만 복사하며 앱 인증 키나 로컬 서버 데이터를 넣지 않는다. 무료 Functions 요청 한도는 기존 Worker와 공유하며 정적 자산은 Function을 거치지 않는다. 근거: https://developers.cloudflare.com/pages/functions/pricing/
