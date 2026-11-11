# Building Draft (Planora)

Kvartirani o'lchash, 2D chizma yaratish va rough 3D modelga tayyor geometriya yaratish platformasi.
Talablar: `Building_Draft_Full_PRD_TZ_v1.0.docx`. Bu repo hozircha **backend va Geometry Core** ni o'z ichiga oladi (Flutter ilova keyingi bosqich).

## Mobil ilova (`mobile/`, Flutter)

Hozircha **2D Constructor PoC** (PRD 15.4): devor chizish, tanlash, burchakni siljitish, o'chirish, undo/redo, pan/zoom, snapping (burchak, devor/T-ulanish, o'q, setka), telefon va planshet layout'i.

```bash
cd mobile
flutter test                 # geometriya, kontroller, vidjetlar, ishlash o'lchovlari, backend shartnomasi
flutter run -d chrome        # ?demo=apartment yoki ?demo=lattice bilan demo reja ochiladi
```

- **Qatlamlar:** `lib/geometry` (Flutter'ga bog'liq emas: model, command'lar, validatsiya, snap, hit-test), `lib/state` (kontroller, viewport), `lib/ui`.
- **Offline validatsiya:** server qoidalarining devorlar qismi Dart'da takrorlangan; o'zgargan devorlar bo'yicha incremental tekshiruv (511 devorda 19 ms o'rniga ~1.3 ms).
- **Shartnoma:** ilovaning chizish seansi `contracts/client_session.json` ga yoziladi (Dart testi), C# testi (`ClientContractTests`) uni serverning haqiqiy geometriya pipeline'ida qayta o'ynaydi. Nom yoki maydon o'zgarsa, ikki tomondan biri yiqiladi. Yangilash: `flutter test --dart-define=UPDATE_GOLDEN=true`.
- **Hali yo'q:** xona/eshik/deraza, o'lchash ekranlari, lokal SQLite va sync, 3D, autentifikatsiya, qurilmada FPS o'lchovi.

## Arxitektura

Modular monolith, qatlamlar bog'liqligi faqat ichkariga qarab yo'nalgan:

```
Api  ->  Application  ->  Domain  <-  Infrastructure
(HTTP)   (use-case'lar)   (qoidalar)   (EF Core, PostgreSQL)
```

| Loyiha | Vazifasi |
|---|---|
| `BuildingDraft.Domain` | Geometry Core, aggregate'lar (`Project`, `DesignVariant`), repository portlari |
| `BuildingDraft.Application` | Use-case servislari (`ProjectService`, `VariantService`, `GeometryService`), DTO'lar |
| `BuildingDraft.Infrastructure` | EF Core mapping, migratsiyalar, repository adapterlari, Unit of Work |
| `BuildingDraft.Api` | Minimal API endpoint'lar, xato kontrakti, composition root |

### Qo'llanilgan patternlar

| Pattern | Qayerda |
|---|---|
| Command | `Geometry/Commands/*` — har bir geometriya o'zgarishi alohida command |
| Strategy + Registry | `Geometry/Constraints/Rules.cs` — har bir constraint turi alohida `ConstraintRule`; solver va validator `switch`siz ishlaydi |
| Pipeline (Chain) | `Geometry/Pipeline` — command → solve → validate → load-bearing ogohlantirish |
| Factory (reflection) | `GeometryCommandCatalog` — `create_wall` → `CreateWall`; yangi command uchun katalog tahrirlanmaydi |
| Aggregate Root | `Project` floor va variant invariantlarini o'zi himoya qiladi |
| Repository + Unit of Work | Portlar Domain'da, adapterlar Infrastructure'da |

## Geometry Core (PRD 8–9, 11-bo'limlar)

- Canonical `GeometryModel`: node, wall, room, opening, constraint. Barcha o'lchamlar mm.
- Constraint solver: deterministik (position-based projection), 10 constraint turi. Hal qilib bo'lmasa `GEOMETRY_CONSTRAINT_CONFLICT` qaytaradi, geometriyani jim o'zgartirmaydi.
- Command'dan keyin foydalanuvchi siljitgan tugunlar mahkamlanadi, qolganlari moslashadi. `FixedPoint` tugunini siljitish konflikt sifatida xabar qilinadi.
- Validatsiya: devor kesishuvi/ustma-ust tushishi, yopiq xona, opening chegaralari va ustma-ust tushishi.
- Xona maydoni ichki yuzalar bo'yicha hisoblanadi; balkon/lojiya (`open_area`) alohida jamlanadi.
- Load-bearing devorga tegilsa ogohlantirish beriladi (muvaffaqiyatli, lekin tasdiqlangan deb ko'rsatilmaydi).

## Versiyalash va himoya

- Har bir variant `1`-versiyadan boshlanadi (bo'sh yoki nusxalangan geometriya). Har bir muvaffaqiyatli command yangi versiya yaratadi.
- `expectedVersion` mos kelmasa `409 VERSION_CONFLICT`. `clientOperationId` takrorlansa command qayta bajarilmaydi, avvalgi natija qaytadi.
- Original Layout `approve` qilingach o'zgartirib bo'lmaydi (`ORIGINAL_LAYOUT_PROTECTED`); variantlar faqat tasdiqlangan Originaldan yaratiladi.
- PostgreSQL darajasida: har bir project uchun bitta Original (unique index), versiya ichidagi bog'lanishlar composite FK bilan himoyalangan.

## Ishga tushirish

```bash
docker compose up -d postgres
ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/BuildingDraft.Api   # migratsiyani o'zi qo'llaydi
dotnet test
```

Docker bilan (API + PostgreSQL):

```bash
export AUTH_SIGNING_KEY="<kamida 32 bayt, maxfiy>"     # yoki AUTH_AUTHORITY=https://sizning-idp
docker compose --profile api up --build
curl http://localhost:8080/health/ready
```

Image non-root foydalanuvchi bilan ishlaydi; eksport fayllari `api-storage` volume'ida. `Database__MigrateOnStartup=true` faqat bitta nusxa (instance) ishlaganda qulay; bir nechta nusxada migratsiyani alohida qadam sifatida bajaring.

CI (`.github/workflows/ci.yml`): build, barcha testlar, model o'zgargan-u migratsiya qo'shilmagan holatni tekshirish (`has-pending-model-changes`) va image'ni haqiqiy PostgreSQL bilan ishga tushirib `/health/ready` ni tekshiruvchi smoke test.

Migratsiya yaratish: `dotnet tool restore && dotnet dotnet-ef migrations add <Nom> -p src/BuildingDraft.Infrastructure -s src/BuildingDraft.Api -o Persistence/Migrations`

## API (`/api/v1`)

| Metod | Yo'l |
|---|---|
| GET, POST | `/projects` (`page`, `pageSize`) |
| GET, PUT, DELETE | `/projects/{id}` (DELETE = arxivlash) |
| GET, POST | `/projects/{id}/floors` · PUT `/floors/{id}` |
| GET, POST | `/projects/{id}/variants` |
| POST | `/variants/{id}/duplicate` · `/variants/{id}/approve` |
| PUT, DELETE | `/variants/{id}` |
| GET | `/variants/{id}/geometry?version=` |
| POST | `/variants/{id}/geometry/commands` · `/variants/{id}/geometry/validate` |
| GET | `/variants/{id}/geometry/versions` |
| POST | `/variants/{id}/geometry/versions/{versionId}/restore` |

O'lchov (Measurement):

| Metod | Yo'l |
|---|---|
| GET, POST | `/projects/{id}/measurements` (`variantId`, `targetObjectId` filtrlari) |
| POST | `/measurements/{id}/apply` (devor uzunligini o'zgartiradi va `FixedLength` bilan mahkamlaydi) |
| PUT | `/measurements/{id}/status` (`verified` / `rejected` / `superseded`) |

AR/LiDAR va AI o'lchovlari taxmin hisoblanadi: ular hech qachon `verified` bo'lmaydi va aniq uzunlik sifatida qo'llanmaydi. Qiymat o'zgarmaydi, tarix o'chirilmaydi.

Offline sync:

| Metod | Yo'l |
|---|---|
| POST | `/sync/push` — operatsiyalar variant bo'yicha tartib bilan; birinchi konflikt/xatodan keyingilar to'xtatiladi |
| POST | `/sync/pull` — klient versiyasidan keyingi o'zgarishlar yoki `snapshotRequired` |
| POST | `/sync/resolve-conflict` — `keep_server` yoki `keep_mine` (hammasi-yoki-hech-narsa) |

Export:

| Metod | Yo'l |
|---|---|
| POST | `/geometry-versions/{id}/exports` — `{ format, floorId? }`; `202 Accepted`, job fon xizmatida bajariladi |
| GET | `/exports/{id}` (`pending` → `processing` → `completed` / `failed`) · `/exports/{id}/download` |

Export asinxron: job bazada saqlanadi, `BackgroundService` bajaradi, server qayta ishga tushsa tugallanmagan joblar qayta navbatga olinadi. Kutilmagan xato faqat shu job'ni `failed` (`EXPORT_FAILED`) qiladi.

Formatlar: **SVG, DXF (R12), PDF, PNG, OBJ**. FBX, DWG, IFC hozircha `EXPORT_FORMAT_NOT_SUPPORTED` qaytaradi. PNG tashqi kutubxonasiz chiziladi (1600 px kenglik, silliq chekka); unda maydon va devor o'lchamlari bor, xona nomlari yo'q (shrift talab qiladi, nomlar kerak bo'lsa SVG yoki PDF). Reja formatlari bitta qavatni chizadi (`floorId`, bitta qavat bo'lsa ixtiyoriy). OBJ metrda, Y yuqoriga; devorlar eshik/deraza atrofida bo'linadi. Fayllar ezdxf, pypdf va trimesh bilan mustaqil tekshirilgan.

Command nomlari: `create_wall`, `move_wall`, `resize_wall`, `update_wall`, `split_wall`, `merge_walls`, `delete_wall`, `create_room`, `delete_room`, `merge_rooms`, `attach_opening`, `remove_opening`, `add_constraint`, `remove_constraint`, `apply_measurement`.

Xato kontrakti: `{ success: false, errorCode, message, traceId, errors?, conflicts?: [{ constraintId, type, message }] }`.

## Kuzatuvchanlik (observability)

- **Health:** `GET /health/live` (jarayon tirik), `GET /health/ready` (+ ma'lumotlar bazasi). Token talab qilinmaydi.
- **Log:** konsolga strukturali JSON (`Logging:Json`, Development'da o'chirilgan).
- **Trace va metrika:** OpenTelemetry (ASP.NET Core, HttpClient, Npgsql, runtime). OTLP'ga eksport faqat `OTEL_EXPORTER_OTLP_ENDPOINT` berilsa yoqiladi.
- **Biznes metrikalar:** `building_draft.geometry.commands` (`command_type`, `outcome`: `applied`/`rejected`/`conflict`) va `building_draft.exports` (`format`, `status`). Teg qiymatlari cheklangan to'plamdan olinadi, mijoz yuborgan nomlar hech qachon teg bo'lmaydi.
- **OpenAPI:** Development'da `GET /openapi/v1.json`.

## Audit log

Muhim amallar (loyiha, qavat, variant, o'lchov, scan, export, tiklash, konflikt yechimi) `audit_log` jadvaliga **o'zgarish bilan bir tranzaksiyada** yoziladi: rad etilgan o'zgarish uchun yozuv qolmaydi. Geometriya command'lari uchun `geometry_operations` audit izi hisoblanadi. O'qish: `GET /api/v1/projects/{id}/audit` (`page`, `pageSize`, yangisi birinchi).

## Autentifikatsiya

Server login/parol saqlamaydi: tashqi identity provider (Keycloak, Auth0, Entra ID va h.k.) chiqargan **JWT Bearer** tokenini tekshiradi. Foydalanuvchi `sub` claim'i bo'yicha `users` jadvalida birinchi so'rovda avtomatik yaratiladi; nofaol foydalanuvchi `403 USER_INACTIVE`.

| Sozlama | Ma'nosi |
|---|---|
| `Auth:Mode` | `Jwt` (default, xavfsiz) yoki `Dev` (bitta soxta foydalanuvchi; faqat `Development` muhitida, boshqa joyda ilova ishga tushmaydi) |
| `Auth:Authority` | OpenID Connect provider manzili (kalitlar avtomatik olinadi) |
| `Auth:SigningKey` | Authority o'rniga umumiy maxfiy kalit (kamida 32 bayt) |
| `Auth:Audience`, `Auth:Issuer` | Token tekshiruvi |

Maxfiy kalitni source code yoki mobil ilovaga yozmang: muhit o'zgaruvchisi (`Auth__SigningKey`) orqali bering.

## Qabul qilingan qarorlar

- Pagination: `page`/`pageSize` (PRD 17.6 da tanlanmagan edi).
- Original Layout `approve`dan keyin qayta ochilmaydi (PRD himoya talab qiladi, ochish qoidasi yozilmagan).
- `apply_measurement` ichki o'lchamni markaz chizig'iga `+ devor qalinligi` bilan o'tkazadi (burchaklar to'g'ri va qalinlik bir xil deb faraz qilinadi).
- `merge_rooms` umumiy devorni xona chegarasidan chiqaradi, lekin devorning o'zini o'chirmaydi (ichki devor sifatida qoladi).
- Har bir versiya to'liq snapshot sifatida saqlanadi (snapshot oralig'i PRD 16.6 bo'yicha keyin sinov asosida).
- Devor balandligi/qalinligi default: 2700 mm / 200 mm.

## Hali qilinmagan

- Serverda foto/video bo'yicha AI inference (scan-session faqat tayyor AR/LiDAR/AI geometriyani import qiladi).
- Export: FBX/DWG/IFC; PDF'da Latin-1 dan tashqari belgilar `?` bo'ladi; DXF R12 (qo'shimcha birlik o'zgaruvchisi yo'q, birlik sarlavha izohida).
- Sync: oddiy metadata uchun last-write-wins (loyiha nomi va h.k. hozir faqat online).
- Token chiqarish (login/parol/ro'yxatdan o'tish): server faqat tashqi identity provider tokenini tekshiradi.
- Constraint `priority`, node `z` koordinatasi, `GeometryOperation` dan versiyani qayta qurish.
- Flutter ilova, AR/LiDAR, Bluetooth lazer adapteri, PoC'lar (PRD Phase 0).
