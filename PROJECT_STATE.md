# 📌 PROJECT ARCHITECTURE & STATE (BẢN GHI TRẠNG THÁI HỆ THỐNG)
> **Mục tiêu:** Lưu trữ bối cảnh kiến trúc và tiến độ của hệ thống Microservices (Web bán giày Adidas).
> **Cách sử dụng để tiết kiệm Token:** Khi bắt đầu phiên làm việc mới hoặc sau lệnh `/compact`, chỉ cần đọc file này để nắm bắt 100% bối cảnh dự án mà không cần scan lại lịch sử chat.

---

## 1. TỔNG QUAN HỆ THỐNG (MICROSERVICES MONOREPO)
- **Repo GitHub:** `https://github.com/duonglthe186124/Project-Services.git` (Nhánh `main`)
- **Mô hình triển khai:** Monorepo, container hóa bằng Docker & Docker Compose, tích hợp CI tự động bằng GitHub Actions.
- **Cơ sở dữ liệu:** MongoDB Atlas (Mỗi microservice sở hữu 1 Database riêng biệt, tuân thủ chuẩn Database-per-service).

---

## 2. TRẠNG THÁI CÁC DỊCH VỤ (SERVICES STATUS)

### ✅ Service 1: `AuthService` (Đã hoàn thành 100%)
- **Thư mục:** `/AuthService`
- **Công nghệ:** .NET 8 Web API, MailKit (SMTP Google), BCrypt, JWT Bearer, MongoDB Driver.
- **Port Docker:** `5265:8080`
- **Database:** MongoDB `AuthDb` (Collections: `Users`, `RefreshTokens`).
- **Chức năng chính đã hoạt động:**
  1. `POST /api/auth/register`: Đăng ký tài khoản, sinh OTP 6 số (mã hóa mật mã học), gửi email HTML chống spam.
  2. `POST /api/auth/verify-otp`: Kích hoạt tài khoản, giới hạn 5 lần thử sai chống brute-force.
  3. `POST /api/auth/resend-otp`: Gửi lại OTP kích hoạt (áp dụng cooldown 60s chống spam/email bombing).
  4. `POST /api/auth/login`: Đăng nhập, cấp cặp Token: Access Token (15 phút) + Refresh Token (30 ngày).
  5. `POST /api/auth/refresh-token`: Làm mới token (cơ chế xoay vòng 1 lần, phát hiện token đánh cắp sẽ thu hồi toàn bộ session).
  6. `POST /api/auth/logout` & `POST /api/auth/logout-all`: Đăng xuất 1 thiết bị hoặc toàn bộ thiết bị.
  7. `POST /api/auth/change-password`: Đổi mật khẩu cho user đã đăng nhập, hủy toàn bộ refresh token cũ.
  8. `POST /api/auth/forgot-password` & `POST /api/auth/reset-password`: Quên mật khẩu qua OTP email.
  9. `POST /api/auth/google`: Đăng nhập bằng Google ID Token.
  10. `GET /api/auth/me`: Lấy thông tin user hiện tại (yêu cầu JWT).
  11. `GET /api/auth/users`: Tra cứu danh sách người dùng, lọc theo `role` (`Customer`, `Admin`, `LogisticsStaff`, `Driver`) và tìm kiếm từ khóa `keyword` (Phân quyền: `[Authorize(Roles = AppRoles.Admin)]` - Sprint 1).
  12. `PATCH /api/auth/users/{id}/role`: Phân quyền / đổi `Role` cho người dùng và tự động thu hồi toàn bộ Refresh Token cũ để ép client làm mới claims token (Phân quyền: `[Authorize(Roles = AppRoles.Admin)]` - Sprint 1).
  13. `POST /api/auth/users/provision-staff`: Khởi tạo/cấp phát nhanh tài khoản nhân sự vận hành (`LogisticsStaff`, `Driver`, `Admin`) đã xác thực email sẵn kèm cặp Access Token + Refresh Token để test ngay luồng nghiệp vụ trên Swagger UI (Phân quyền: `[Authorize(Roles = AppRoles.Admin)]` - Sprint 1).
- **Phân quyền Role (`AppRoles`):** Chuẩn hóa 4 vai trò: `"Customer"` (mặc định), `"Admin"`, `"LogisticsStaff"` (Nhân viên kho/điều phối vận chuyển 3PL), `"Driver"` (Tài xế giao hàng).

---

### ✅ Service 2: `CatalogService` (Đã hoàn thành 100% + Nâng cấp Khóa Kho Nguyên Tử Sprint 1)
- **Thư mục:** `/CatalogService`
- **Công nghệ:** .NET 8 Web API, MongoDB Driver, JWT Bearer, Swagger, BackgroundService (`StockReservationExpirationWorker`).
- **Port Docker:** `5266:8080` (Cổng local HTTP: `5266` / `5000`)
- **Database:** MongoDB `CatalogDb` (Collections: `Products`, `StockReservations`).
- **Chức năng chính đã hoạt động:**
  1. `GET /api/catalog/products`: Tra cứu danh mục, lọc theo `keyword`, `category`, `gender`, `color`, `size` (chỉ lấy size còn hàng), `minPrice`/`maxPrice`, `onSale`, `tag`, `isActive` (mặc định chỉ lấy sản phẩm đang bán, hỗ trợ xem sản phẩm bị ẩn), sắp xếp (`newest`, `price_asc`, `price_desc`, `rating`, `popular`), phân trang (`Page`, `PageSize`). (Phân quyền: Public / Admin)
  2. `GET /api/catalog/products/{id}`: Xem chi tiết đôi giày, bảng size tồn kho (`Quantity` khả dụng & `ReservedQuantity` đang tạm giữ), album ảnh, đánh giá (hỗ trợ cờ `includeInactive=true`). (Phân quyền: Public)
  3. `GET /api/catalog/filters`: Lấy metadata bộ lọc (dòng giày, màu sắc, size còn hàng, khoảng giá Min/Max). (Phân quyền: Public)
  4. `POST /api/catalog/products/{id}/reviews`: Gửi đánh giá sao (1-5) & nhận xét, tự động tính lại `AverageRating` và `ReviewCount`. (Phân quyền: `[Authorize(Roles = "Customer, Admin")]`)
  5. `POST /api/catalog/products`: Thêm mẫu giày mới vào cửa hàng (hỗ trợ cờ `IsActive = true` mặc định). (Phân quyền: `[Authorize(Roles = "Admin")]`)
  6. `PUT /api/catalog/products/{id}`: Cập nhật thông tin, giá, bảng size tồn kho (tự động bảo toàn `ReservedQuantity` của các đơn đang giữ hàng), trạng thái `IsActive`. (Phân quyền: `[Authorize(Roles = "Admin")]`)
  7. `DELETE /api/catalog/products/{id}`: Xóa mẫu giày (Mặc định: Xóa mềm `isActive=false`. Truyền `hardDelete=true`: Xóa vĩnh viễn khỏi Database). (Phân quyền: `[Authorize(Roles = "Admin")]`)
  8. `PATCH /api/catalog/products/{id}/restore`: Khôi phục lại mẫu giày đã ẩn về trạng thái mở bán. (Phân quyền: `[Authorize(Roles = "Admin")]`)
  9. **Tự động Seeder:** Khởi tạo sẵn 6 mẫu giày Adidas thật (Ultraboost Light, Samba OG, Stan Smith, Predator Elite, Forum Low, Dropset 2) kèm ảnh thật, giá và review mẫu khi khởi động.
  10. **Bộ API Điều phối Tồn kho Nguyên tử (Saga / Internal Stock Reservation Engine - Sprint 1):**
      - `POST /api/catalog/internal/stock/reserve`: Khóa tồn kho nguyên tử theo `OrderId` + danh sách `(ProductId, Size, Quantity)` với TTL mặc định 15 phút. Triệt tiêu rủi ro Overselling F-01 bằng toán tử `FindOneAndUpdateAsync` với `ElemMatch`, Idempotent theo `ux_orderId`, tự động Rollback All-or-Nothing nếu 1 món hết hàng, trả về Snapshot giá và ảnh chuẩn. (Phân quyền: **Internal Server-to-Server / Saga Step 1** - Chặn Public ở Gateway).
      - `POST /api/catalog/internal/stock/confirm`: Xác nhận xuất kho chính thức sau khi thanh toán thành công hoặc duyệt đơn COD. Trừ `ReservedQuantity` và đổi trạng thái bản ghi sang `Confirmed`. (Phân quyền: **Internal Server-to-Server / Saga Step 2a** - Chặn Public ở Gateway).
      - `POST /api/catalog/internal/stock/release`: Nhả tồn kho bù trừ (Compensating Transaction) cộng ngược lại vào kệ khi đơn hủy, lỗi thanh toán, quá hạn 15 phút hoặc hoàn hàng RTO. Xử lý Idempotent 100% không cộng thừa kho. (Phân quyền: **Internal Server-to-Server / Saga Compensation** - Chặn Public ở Gateway).
      - `GET /api/catalog/internal/stock/reservations/{orderId}`: Tra cứu chi tiết phiên giữ hàng theo OrderId (thời điểm khóa, size tạm giữ, hạn TTL `ExpiresAt`, trạng thái). (Phân quyền: **Internal Inspection** - Chặn Public ở Gateway).
      - `GET /api/catalog/internal/stock/reservations`: Tra cứu danh sách các phiên giữ hàng trong kho, hỗ trợ lọc theo status (`Reserved`, `Confirmed`, `Released`, `Expired`) phục vụ giám sát kho và kiểm toán. (Phân quyền: **Internal Management / Admin & LogisticsStaff**).
      - `POST /api/catalog/internal/stock/cleanup-expired`: Kích hoạt thủ công quét và giải phóng tồn kho cho các đơn quá hạn TTL 15 phút phục vụ test nhanh trên Swagger mà không cần đợi cron. (Phân quyền: **Internal Maintenance / Admin & Dev Tool**).
      - `StockReservationExpirationWorker` (`BackgroundService`): Tiến trình ngầm tự động quét mỗi 30 giây để thu hồi tồn kho của các phiên giữ hàng quá hạn 15 phút (`Self-Healing TTL Guard`).

---

### ⏳ Giai đoạn 2: Chuỗi Đơn hàng – Kiểm soát Rủi ro Thanh toán – Vận chuyển 3PL
> 📄 **Tài liệu Thiết kế Chi tiết & Bàn giao cho Đội Dev:** Xem tại [`NEXT_SERVICES_BLUEPRINT.md`](./NEXT_SERVICES_BLUEPRINT.md)

- **Sprint 1 (Đã hoàn thành 100%):** Nâng cấp `AuthService` (RBAC cho `LogisticsStaff` & `Driver`) và `CatalogService` (Atomic Stock Reservation Engine, Idempotency & Self-Healing TTL Worker).
- **Service 3 (Sprint 2): `OrderService` + `Workflow Engine (Saga State Machine)` (Port `5267:8080`, DB: `OrderDb`) - [TIẾN ĐỘ: ✅ ĐÃ HOÀN THÀNH 100% (M1, M2, M3, M4) - 219/219 TESTS PASS (OrderService.Tests 207 + OrderService.Models.Tests 12)]**:
  - **Công nghệ:** .NET 8 Web API, MongoDB Atlas, Polly.Core (Resilience Pipeline), JWT Bearer RBAC, Swagger Bearer Auth.
  - **Database:** MongoDB `OrderDb` với 5 Collections và 9 Indexes tự động (`OrderDbInitializer`):
    1. `Carts` (Unique index `ux_userId`): Giỏ hàng của từng khách hàng, chống race condition tạo trùng giỏ.
    2. `Orders` (Unique index `ux_orderCode`, Compound index `ix_userId_createdAt`, Index `ix_orderStatus`): Lưu trữ đơn hàng chính thức sau khi khóa kho thành công (Atomic Stock Reservation).
    3. `OrderSagaStates` (Unique index `ux_orderId`, Compound index `ix_currentState_expiresAt`): Nhạc trưởng Saga in-process, lưu `Version` kiểm soát xung đột đồng thời (OCC) và Sổ cái kiểm toán bất biến `AuditTrail`.
    4. `OrderCompensations` (Index `ix_status_nextRetryAt`): Transactional Outbox lưu các giao dịch bù trừ kho khi Catalog gặp sự cố để background worker retry tự phục hồi (Self-Healing).
    5. `IdempotencyRecords` (Unique index `ux_key`, TTL index `ttl_expiresAt` 24h): Khóa chống click đúp / trừ kho 2 lần khi checkout.
  - **Đã hoàn thành Milestone 1 (Foundation & Polly Client):**
    - Hoàn tất Models, DB Context, DB Initializer (9 indexes).
    - `CatalogServiceClient` bọc bởi Polly (3 retry exponential backoff + jitter, Circuit Breaker).
  - **Đã hoàn thành Milestone 2 (Cart Domain & API Endpoints - Đã fix 100% 2 lỗi kiểm duyệt):**
    - *Lỗi 1 đã sửa:* Thêm `[property: ...]` cho toàn bộ DataAnnotation trên positional record `CartDtos.cs` để ASP.NET Core Model Validator kích hoạt đúng trên Property.
    - *Lỗi 2 đã sửa:* Dùng `MongoDB.Bson.ObjectId.TryParse` trong `CartsController.cs` và custom attribute `[property: MongoObjectId]`, loại bỏ lỗ hổng kiểm tra độ dài chuỗi `.Length != 24`.
    - Đã vượt qua 145/145 tests tự động (`OrderService.Tests` và `OrderService.Models.Tests`) với live MongoDB Atlas.
  - **Bộ API Giỏ hàng đã hoạt động (`CartsController.cs` - Phân quyền `Customer, Admin`):**
    1. `GET /api/carts/my-cart`: Lấy giỏ hàng của user hiện tại (Không có URL param, đọc UserId từ token).
    2. `POST /api/carts/items`: Thêm sản phẩm vào giỏ (Body JSON `AddToCartRequest`, không có URL param).
    3. `PUT /api/carts/items/{productId}/{size}`: Cập nhật số lượng mặt hàng (**CÓ tham số URL**: `{productId}` 24 hex và `{size}` 30-55; Body JSON `{ quantity }`).
    4. `DELETE /api/carts/items/{productId}/{size}`: Xóa một mặt hàng khỏi giỏ (**CÓ tham số URL**: `{productId}` và `{size}`).
    5. `DELETE /api/carts/clear`: Xóa toàn bộ giỏ hàng (Không có URL param).
  - **Đã hoàn thành Milestone 3 (Saga Engine OCC + Cancel + Payment Callback):**
    - **File mới:** `Services/SagaPolicies.cs` (ma trận `SagaTransitions`, `PaymentCallbackPolicy`, `OrderAccessPolicy` – logic thuần, test không cần Mongo), `Services/IOrderSagaOrchestrator.cs` + `OrderSagaOrchestrator.cs`, `Services/OrderQueryService.cs`, `Dtos/OrderDtos.cs`, `Controllers/OrdersController.cs`, `Controllers/InternalOrdersController.cs`, `OrderService.Tests/OrderSagaTests.cs` (40 test).
    - **Sửa:** `Order` thêm `PaymentTransactionId`, `PaidAt`. `SagaTriggerConstants` thêm `PaymentCallbackFailed`, `StockConfirmed`, `StockConfirmRejected`. `Program.cs` đăng ký DI 2 service mới. `appsettings.json` thêm `InternalApi:Key` (để trống, cấu hình qua env `InternalApi__Key`).
    - **Lõi OCC – `TryTransitionAsync`:** chặn transition ngoài ma trận → 1 lệnh `FindOneAndUpdateAsync` filter `orderId + currentState (+ version)`, `$set currentState`, `$inc version`, `$push auditTrail` → đồng bộ `Orders.orderStatus`. Thua OCC thì trả Conflict và **không gọi Catalog**.
    - **Ma trận đang dùng:** `AwaitingPayment → PaymentVerified | Cancelled | Expired`; `PaymentVerified → ReadyForDispatch | LatePaymentDetected`; `Expired | Cancelled → LatePaymentDetected`; chuỗi giao hàng `ReadyForDispatch → WarehousePacking → CarrierAssigned → InTransit → Delivered → Completed` (dành cho M6/Sprint 4).
    - **Bộ API đơn hàng (`OrdersController`, `[Authorize]`):**
      1. `GET /api/orders/my-orders?page=1&pageSize=10` (Customer, Admin): đơn của chính mình, mới nhất trước. Query param, pageSize tối đa 50.
      2. `GET /api/orders?status=&page=&pageSize=` (Admin, LogisticsStaff): toàn bộ đơn, lọc theo status. Status sai → 400.
      3. `GET /api/orders/{id}` (**URL param** `id` ObjectId): chủ đơn hoặc Admin/LogisticsStaff. Người khác nhận **404** (không lộ đơn tồn tại). `DeliveryOtpCode` chỉ chủ đơn thấy, `SecuritySealCode` chỉ staff thấy. Status lấy từ Saga (nguồn sự thật).
      4. `GET /api/orders/{id}/timeline` (**URL param** `id`): AuditTrail + version, cùng quyền như trên.
      5. `POST /api/orders/{id}/cancel` (**URL param** `id`, body `{reason}` ≤500 ký tự; Customer chủ đơn / Admin): chỉ khi `AwaitingPayment`, sai state → 409. Hủy xong gọi Catalog release; lỗi thì ghi `OrderCompensations` (Pending, retry sau 20s bởi worker M5) và vẫn trả 200 với `compensationQueued:true`.
    - **API nội bộ (`InternalOrdersController`):** `POST /api/orders/internal/payment-callback`. Không dùng JWT, bảo vệ bằng header `X-Internal-Api-Key` (so sánh constant-time). Key trống thì cho qua và log cảnh báo (chỉ dùng cho dev).
      - Body: `{orderId, paymentTransactionId, amount, paymentMethod?, callbackTimestamp?, status: "Success"|"Failed"}`.
      - `AwaitingPayment` + đúng tiền → `PaymentVerified` → Catalog confirm → `ReadyForDispatch` (200).
      - Catalog từ chối confirm (409/404, kho đã nhả) → `LatePaymentDetected`, `refundRequired:true`.
      - Catalog lỗi mạng → giữ `PaymentVerified`, `failureReason="ConfirmPending..."`, trả **202** (M5 confirm lại).
      - Đơn `Expired`/`Cancelled` → `LatePaymentDetected`, **tuyệt đối không confirm kho**, `refundRequired:true`.
      - Cùng `paymentTransactionId` gửi lại → 200 `isReplay:true`. Khác transaction trên đơn đã trả tiền → 409. Lệch `amount` → 400. `status=Failed` → chỉ ghi audit, giữ `AwaitingPayment`.
      - Thua OCC (callback và cancel chạy cùng lúc) thì tự đánh giá lại 1 lần theo state mới.
    - **Bài học (golden rule mới):** `[Range(typeof(decimal), "0.01", ...)]` phải có `ParseLimitsInInvariantCulture = true`, vì máy culture `vi-VN` sẽ ném `FormatException`.
    - **Nợ kỹ thuật chuyển sang M5:** (1) Lệch tạm thời `Orders.orderStatus` so với Saga nếu crash giữa 2 lệnh update, cần reconcile. (2) Đơn kẹt `PaymentVerified` có `failureReason` bắt đầu bằng `ConfirmPending`, worker phải confirm lại. (3) Hai callback trùng nhau đến trong khoảng vài ms giữa transition và lúc ghi `PaymentTransactionId` có thể nhận 409 thay vì replay; PaymentService (Sprint 3) cần retry callback khi gặp 409/5xx. (4) Test `PollyPipeline_Retry_DelaysFollowExponentialProgression` thỉnh thoảng flaky do đo thời gian.
    - **Test thủ công:** từ M4 đã có checkout nên không cần seed Mongo: thêm hàng vào giỏ → `POST /api/orders/checkout` → gọi `payment-callback`. Mẫu request có trong `OrderService.http`.
    - ⚠️ *Ghi chú cập nhật (25/10):* các nhắc tới "worker M5" ở trên (cancel retry qua `OrderCompensations`, `ConfirmPending`, reconcile) là thiết kế cũ; M5 đã **bị lược bỏ**. `OrderCompensations` vẫn được ghi khi release lỗi nhưng chưa có worker xử lý (Catalog TTL 15 phút là lưới an toàn).
  - **Cẩm nang triển khai tiếp theo bằng Single Agent (Vibe Code - LỘ TRÌNH RÚT GỌN TIẾT KIỆM TOKEN & QUOTA - Cập nhật 25/10):**
    - **Quyết định cắt gọn phạm vi (Descoping):**
      1. **Bỏ toàn bộ luật phân tầng rủi ro (Tiered Risk Policies):** Không áp luật `>= 5M` (HighValue), không áp luật `>= 15M` / `>= 4 đôi` (VipBulk), không cần API duyệt đơn thủ công (`approve`), không dùng `DeliveryOtpCode` hay `SecuritySealCode`. Chỉ cần tạo đơn hợp lệ và chọn **`COD`** hoặc **`Online_QR`** (chuyển tiền trước).
      2. **Bỏ `ShippingService` (Service 5) & Background Workers phức tạp (M5 cũ):** Tận dụng luôn `StockReservationExpirationWorker` có sẵn bên `CatalogService` để tự nhả kho sau 15 phút. Thay thế toàn bộ `ShippingService` bằng việc tự động sinh **Mã vận đơn (`WaybillCode`, vd: `WB-yyyyMMdd-XXXXXX`)** và **gửi Email xác nhận đơn hàng + vận đơn** ngay tại `OrderService`.
      3. **Tái sử dụng `MailKit` + `MimeKit` từ `AuthService`:** Cấu hình `EmailSettings` giống hệt `AuthService`, thay khung mã OTP bằng bảng chi tiết đơn hàng (mã đơn, mã vận đơn, tên người nhận, SĐT, địa chỉ giao hàng, danh sách sản phẩm, size, đơn giá, số lượng, tổng tiền).
    - **Milestone 3 (✅ ĐÃ HOÀN THÀNH 25/10 – 185/185 tests pass):** Saga Engine OCC + Hủy đơn + Payment Callback.
    - **Milestone 4 – Checkout + Mã vận đơn + Email (✅ ĐÃ HOÀN THÀNH 25/10 – 219/219 tests pass, `OrderService` xong 100%):**
      - **File mới:** `Dtos/CheckoutDtos.cs` (`CheckoutRequest`, `CheckoutAddressRequest`, `CheckoutItemRequest`, `CheckoutResponse`, `CheckoutValidation`), `Services/IOrderCheckoutService.cs` + `OrderCheckoutService.cs`, `Services/OrderCodeGenerators.cs` (`OrderCodeGenerator` → `ADI-yyyyMMdd-XXXXXX`, `WaybillCodeGenerator` → `WB-yyyyMMdd-XXXXXX`, CSPRNG, bỏ ký tự dễ nhầm I/O/0/1), `Services/IOrderEmailService.cs` + `OrderEmailService.cs` (MailKit) + `OrderEmailBuilder.cs` (dựng HTML + plain-text, `HtmlEncode` mọi dữ liệu người dùng), `OrderService.Tests/CheckoutAndEmailTests.cs` (34 test).
      - **Sửa:** `Order` thêm `CustomerEmail`, `WaybillCode`; `OrderDetailDto` thêm `CustomerEmail`, `WaybillCode` (tham số cuối, có default); `OrderResultKind` thêm `ServiceUnavailable`; `OrderSagaOrchestrator` (ctor nhận thêm `IOrderEmailService?` tùy chọn, validate `PaymentMethod`, ghi `PaymentMethod`, `FinalizeDispatchAsync`); `OrdersController` thêm `POST checkout`; `Program.cs` đăng ký DI; `OrderService.csproj` thêm `MailKit`/`MimeKit` 4.18.1; `appsettings.json` thêm block `EmailSettings`; `OrderService.http` thêm mục M4.
      - **`POST /api/orders/checkout` (`[Authorize(Roles="Customer,Admin")]`, không có URL param, body JSON):** `customerPhone` (regex SĐT VN), `shippingAddress{addressLine,ward,district,city,latitude?,longitude?}`; tùy chọn `customerName` (mặc định claim `fullName`), `customerEmail` (mặc định claim `email`), `items[]{productId,size,quantity}` (tối đa 50 dòng; có `items` thì mua ngay và **không đụng giỏ hàng**, bỏ trống thì lấy giỏ hàng và **dọn giỏ sau khi tạo đơn**). Dòng trùng `(productId,size)` được gộp, tổng > 100 → 400.
        - Luồng: validate → gộp dòng → sinh `orderId` + `orderCode` (kiểm tra trùng) → Catalog `reserve` 15 phút → dựng `Order` từ **snapshot giá Catalog** (zero-trust, không tin giá client; `ShippingFee = 0`, `PaymentMethod` để trống chờ PaymentService) → insert `Orders` + `OrderSagaStates` (`AwaitingPayment`, version 1, audit `CheckoutSuccess`) → dọn giỏ → **201** `CheckoutResponse` (kèm `expiresAt`).
        - Lỗi: thiếu tên/email → 400; hết hàng → **409** (message liệt kê size còn lại); `InvalidProductId` → 400; Catalog chết/circuit mở → **503**; insert thất bại → rollback (xóa order) + gọi Catalog `release`. **Không gửi email ở bước này.**
      - **`payment-callback` nâng cấp:** `paymentMethod` phải thuộc `COD|Online_QR|Online_Card|EWallet` (sai → 400), được ghi vào `Orders.paymentMethod`. Khi transition sang `ReadyForDispatch` thành công (không phải replay): sinh `WaybillCode` (chỉ ghi nếu chưa có) rồi gọi `IOrderEmailService`. Lỗi mã vận đơn/email chỉ log, **callback vẫn trả 200**.
      - **Email (`OrderEmailService`):** cùng khóa cấu hình như `AuthService`: `EmailSettings:SmtpServer|SmtpPort|SenderName|SenderEmail|AppPassword` (env: `EmailSettings__SenderEmail`, `EmailSettings__AppPassword`). Thiếu cấu hình / không có email / SMTP lỗi → trả `false`, log cảnh báo (Development in nội dung ra Console), **không bao giờ ném exception**. Nội dung: mã đơn, mã vận đơn, tên, SĐT, địa chỉ, bảng sản phẩm (size, đơn giá, SL, thành tiền), tạm tính, phí ship, tổng, phương thức thanh toán. Subject được loại CR/LF chống header injection.
      - **Cách chạy thử email thật:** copy `SenderEmail` + `AppPassword` (Gmail App Password) từ cấu hình `AuthService` sang `OrderService/appsettings.Development.json` (hoặc biến môi trường); không commit mật khẩu.
      - **Giới hạn đã chấp nhận (do cắt gọn):** (1) Không còn worker `OrderSagaTimeoutWorker`: đơn quá 15 phút vẫn nằm `AwaitingPayment` trong DB (Catalog đã tự nhả kho); nếu thanh toán đến muộn → Catalog từ chối confirm → `LatePaymentDetected` (cần hoàn tiền). (2) Nếu Catalog lỗi mạng lúc confirm → đơn kẹt `PaymentVerified` (202), PaymentService nên gửi lại callback. (3) Email gửi đồng bộ trong callback (timeout SMTP 15s); nếu muốn nhanh hơn có thể chuyển sang fire-and-forget sau.
      - **Việc cần làm tiếp theo (Sprint 3):** `PaymentService` (xem bên dưới). Cần user cung cấp: tên ngân hàng (BankId VietQR), số tài khoản, tên chủ tài khoản, và `SePayApiKey`.
- **Service 4 (Sprint 3 - Tinh gọn, ✅ ĐÃ HOÀN TẤT & VERIFIED TOÀN TRÌNH 100%): `PaymentService` (Port `5268:8080`, DB: `PaymentDb`)**:
  - **Kết quả nghiệm thu thực tế E2E (28/10):**
    - Đã test thành công 100% kịch bản **COD**: Tạo đơn -> `POST /api/payments/process-cod` -> `OrderService` chuyển `ReadyForDispatch`, sinh mã vận đơn `WB-20261028-MS3PJS` và bắn Email HTML thật qua MailKit SMTP về `pot803709@gmail.com`.
    - Đã test thành công 100% kịch bản **Online QR (VietQR + SePay Webhook)**: Tạo đơn -> `POST /api/payments/create-qr` (sinh link VietQR chuẩn Napas247) -> `POST /api/payments/webhook/sepay` giả lập ngân hàng chuyển tiền -> tự động trích xuất mã `ADI-20261028-D7LXHX` -> `OrderService` chuyển `ReadyForDispatch`, sinh mã vận đơn `WB-20261028-ALKNLH` và bắn Email HTML thật qua MailKit SMTP về `pot803709@gmail.com`.
    - Lịch sử giao dịch lưu vết thành công trong `PaymentDb.PaymentTransactions`.
  - **Cấu trúc mã nguồn (`g:\c#\AuthService\PaymentService`):**
    - `PaymentService.csproj`, `Program.cs`, `Properties/launchSettings.json` (chạy tại `http://localhost:5268/swagger`), `appsettings.json`, `appsettings.Development.json`, `PaymentService.http`.
    - `Models/PaymentTransaction.cs`: Lưu lịch sử giao dịch trong collection `PaymentTransactions` (`PaymentDb`).
    - `Data/PaymentDbInitializer.cs`: Tự động tạo 4 chỉ mục khi khởi động: `ux_transactionCode` (Unique), `ix_orderId_createdAt`, `ix_orderCode`, và `ux_sePayTransactionId` (Unique Partial Index khi `sePayTransactionId > 0`).
    - `Dtos/PaymentDtos.cs`: `ProcessCodRequest`, `CreateQrRequest`, `PaymentTransactionDto`, `SePayWebhookPayload`, `SePayWebhookResponse`, `OrderPaymentCallbackPayload`.
    - `Services/VietQrService.cs`, `Services/OrderCallbackClient.cs` (Polly retry 3 lần khi gặp lỗi mạng / 5xx / 408 / 409), `Services/PaymentProcessingService.cs`.
    - `Controllers/PaymentsController.cs` (4 API endpoints):
      1. `POST /api/payments/process-cod` (`[Authorize(Roles="Customer,Admin")]`): Khách chọn COD -> lưu giao dịch COD -> gọi ngay `payment-callback` sang `OrderService` -> đơn sang `ReadyForDispatch`, sinh `WaybillCode` và gửi Email xác nhận.
      2. `POST /api/payments/create-qr` (`[Authorize(Roles="Customer,Admin")]`): Khách chọn `Online_QR` -> lưu giao dịch `Pending` -> trả về URL ảnh QR chuẩn Napas247 từ `VietQR.io`.
      3. `POST /api/payments/webhook/sepay` (`[AllowAnonymous]`, bảo vệ bằng `SePayApiKey`): Nhận biến động số dư từ SePay -> lọc mã `ADI-...` -> đối chiếu số tiền -> gọi `payment-callback` sang `OrderService` -> tự động duyệt đơn & gửi Email.
      4. `GET /api/payments/order/{orderId}` (`[Authorize]`): Tra cứu lịch sử thanh toán của đơn hàng.
  - **📌 CÁC ĐIỀU LƯU Ý QUAN TRỌNG KHI BẮT ĐẦU & VẬN HÀNH `PaymentService`:**
    1. **Ngân hàng tự xóa dấu gạch ngang `-` trong nội dung chuyển khoản:** Nhiều ngân hàng (MBBank, VCB...) khi quét QR sẽ tự động đổi `ADI-20261027-ENGAW2` thành `ADI20261027ENGAW2` hoặc `ADI 20261027 ENGAW2`. Hàm `VietQrService.ExtractNormalizedOrderCode` dùng Regex `ADI[\s\-]*(\d{8})[\s\-]*([A-Za-z0-9]{6})` tự động bắt mọi biến thể và chuẩn hóa lại thành `ADI-yyyyMMdd-XXXXXX` để luôn tìm đúng đơn hàng trong DB.
    2. **Constructor mặc định trên C# Record DTO & Culture `vi-VN`:** Toàn bộ Request Record trong `PaymentDtos.cs` đều được khai báo constructor rỗng `public Record() : this(...)` (tránh lỗi `ThrowIfRecordTypeHasValidationOnProperties` của ASP.NET Core 8) và bật `ParseLimitsInInvariantCulture = true` cho `RangeAttribute` kiểu `decimal`.
    3. **Sửa lỗi CultureNotFoundException khi chạy Invariant Mode:** `OrderEmailBuilder` đã được nâng cấp dùng `NumberFormatInfo` thuần thay cho `CultureInfo.GetCultureInfo("vi-VN")`, giúp tương thích 100% với môi trường `<InvariantGlobalization>true</InvariantGlobalization>` khi gửi Email format tiền Việt.
    4. **Xác thực SePay API Key linh hoạt (Hỗ trợ cả SePay thật lẫn Swagger UI):**
       - SePay thật gửi header `"Authorization": "Apikey sepay_adidas_secret_2026"`.
       - Khi test thủ công trên Swagger UI: Endpoint `POST /api/payments/webhook/sepay` có sẵn ô nhập header `X-SePay-Api-Key` để dán nhanh `sepay_adidas_secret_2026` mà không bị xung đột với nút Authorize Bearer JWT của Swagger.
    5. **Lưu ý chặn `localhost` trên SePay.vn & Cách nối bằng Ngrok:**
       - Hệ thống SePay không cho phép nhập URL Webhook là `localhost` hoặc IP Private. Lúc tạo Webhook trên SePay, dùng URL công khai tạm thời (như `https://httpbin.org/post`) để lấy/lưu API Key.
       - Khi test trên máy tính cá nhân: có thể bấm trực tiếp trên Swagger `http://localhost:5268/swagger`. Khi muốn biểu diễn quét QR chuyển tiền thật trên điện thoại, chạy `ngrok http 5268` và dán link `https://xxxx.ngrok-free.app/api/payments/webhook/sepay` vào SePay.
    6. **Đồng bộ `EmailSettings` cho `OrderService`:** Đã bổ sung `SenderEmail` và `AppPassword` vào `OrderService/appsettings.Development.json` để đảm bảo khi `PaymentService` gọi `payment-callback` sang `OrderService`, khách hàng nhận được ngay Email HTML kèm mã vận đơn `WB-...`.
    7. **Bảo mật thông tin nhạy cảm qua file `.env` (Không hardcode trong `appsettings*.json`):** Toàn bộ `ConnectionString`, `JwtSettings__Secret`, `VietQrSettings__BankId|AccountNo|AccountName`, và `SePaySettings__ApiKey` của `PaymentService` đều để trống trong `appsettings.json` / `appsettings.Development.json` và được nạp tự động từ file `g:\c#\AuthService\.env` qua hàm `LoadDotEnvFile()` trong `PaymentService/Program.cs`.
    8. **🛡️ Đã vá 5 Lỗ hổng Bảo mật Nghiệp vụ Thanh toán (Zero-Trust Payment Hardening - 28/10):**
       - **Vá Lỗ hổng 1 (Chống sửa giá Amount Tampering & Toàn vẹn trạng thái Callback):** Khi khách gọi `POST /api/payments/create-qr` hoặc `POST /api/payments/process-cod`, `PaymentService` gọi trực tiếp `GET /api/orders/{orderId}` sang `OrderService` (kèm Bearer Token của khách) để lấy `TotalAmount` và `OrderCode` gốc. Nếu `request.Amount != snapshot.TotalAmount` -> chặn đứng ngay với **HTTP 400 Bad Request**. Đồng thời trong `HandleSePayWebhookAsync`, chỉ đánh dấu `tx.Status = Success` khi `OrderService` trả về `callbackResult.HttpSuccess == true`.
       - **Vá Lỗ hổng 2 (Chống IDOR / Broken Object Level Authorization):** Kiểm tra chặt quyền sở hữu đơn hàng (`UserId` trong JWT phải khớp `Order.UserId` / `PaymentTransaction.UserId`, trừ `Admin`/`LogisticsStaff`) trên cả 3 API: `POST /api/payments/create-qr`, `POST /api/payments/process-cod`, và `GET /api/payments/order/{orderId}` (trả về **HTTP 403/404** nếu cố tình xem hoặc thanh toán đơn của người khác).
       - **Vá Lỗ hổng 3 (Fail-Closed SePay Webhook & Khóa Nội Bộ `X-Internal-Api-Key`):** Loại bỏ cơ chế Fail-Open trên `POST /api/payments/webhook/sepay` (nếu thiếu cấu hình `SePaySettings:ApiKey` sẽ trả **HTTP 503** thay vì cho qua); loại bỏ việc bóc tách tiền tố `Bearer ` trong `ExtractSePayApiKey` (chỉ chấp nhận `Authorization: Apikey <key>` hoặc `X-SePay-Api-Key`); đồng bộ khóa `OrderService__InternalApiKey` = `InternalApi__Key` = `internal_adidas_saga_secret_2026` giữa `PaymentService` và `OrderService`.
       - **Vá Lỗ hổng 4 (Rate Limiting Chống Brute-Force SePay Webhook):** Áp dụng Partitioned Rate Limiter riêng cho nhánh `/api/payments/webhook/sepay` với giới hạn nghiêm ngặt **20 requests/phút/IP** (`QueueLimit = 0`, trả về **HTTP 429 Too Many Requests**) tại cả `ApiGateway` và `PaymentService`.
       - **Vá Lỗ hổng 5 (Xác thực Số Tài Khoản Ngân Hàng Đích):** Trong `HandleSePayWebhookAsync`, đối chiếu `payload.AccountNumber` từ SePay với `_vietQr.AccountNo` của cửa hàng; nếu lệch trả về ngay **HTTP 400 Bad Request**.
- ~~**Service 5 (Sprint 4): `ShippingService`**~~ -> **ĐÃ LƯỢC BỎ** (Thay thế bằng cơ chế tự động sinh `WaybillCode` + gửi Email hóa đơn/vận đơn qua `MailKit` ngay trong `OrderService`).
- **Service 6 (✅ ĐÃ HOÀN TẤT & VERIFIED TOÀN TRÌNH 100%): `ApiGateway` (Port `5000:8080`)**:
  - **Công nghệ & Kiến trúc:**
    - YARP (Yet Another Reverse Proxy) .NET 8, chạy cổng `http://localhost:5000/swagger`.
    - Đóng vai trò là Cửa ngõ duy nhất (Single Point of Entry) cho toàn bộ Client (Frontend Web React/Vue/Flutter, Mobile App, SePay Webhook).
    - Tập trung quản lý CORS (`AllowAll` cho dev), Rate Limiter phân tầng (150 req/phút cho API chung; **20 req/phút/IP** chống brute-force riêng cho `/api/payments/webhook`), và Aggregated Swagger UI hiển thị thông tin 4 microservices.
  - **Lá chắn an ninh 2 lớp (Defense-in-Depth):**
    1. **`InternalEndpointBlockerMiddleware`:** Chặn đứng 100% mọi request từ ngoài Internet cố tình truy cập vào các endpoint nội bộ chứa segment `/internal/` (khoảng giữ kho `/api/catalog/internal/stock/reserve`, xuất kho `/confirm`, nhả kho `/release`, và callback thanh toán `/api/orders/internal/payment-callback`). Trả về ngay **HTTP 404 Not Found** (chuẩn Zero-Trust để đánh lừa kẻ tấn công).
    2. **Chống giả mạo Header (`X-Internal-Api-Key`):** Tự động bóc gỡ (`strip`) toàn bộ header nội bộ nếu client bên ngoài cố tình gửi lên.
  - **Kết quả nghiệm thu thực tế E2E (28/10):**
    - `GET http://localhost:5000/health`: Healthy.
    - `GET http://localhost:5000/api/catalog/products`: Forward thành công sang CatalogService (7 sản phẩm).
    - `POST /api/catalog/internal/stock/reserve|confirm|release`: Bị chặn đứng 100% (HTTP 404).
    - `POST /api/orders/internal/payment-callback`: Bị chặn đứng 100% (HTTP 404).
    - `POST /api/orders/checkout` & `POST /api/payments/create-qr`: Thực thi mượt mà qua cổng 5000.

---

## 3. CẤU TRÚC BIẾN MÔI TRƯỜNG CHUNG (`.env`)
```ini
ASPNETCORE_ENVIRONMENT=Development
# Auth Database
MongoDbSettings__ConnectionString=mongodb+srv://...
MongoDbSettings__DatabaseName=AuthDb
# Catalog Database
CatalogDbSettings__ConnectionString=mongodb+srv://...
CatalogDbSettings__DatabaseName=CatalogDb
# Order Database
OrderDbSettings__ConnectionString=mongodb+srv://...
OrderDbSettings__DatabaseName=OrderDb
# Payment Database
PaymentDbSettings__ConnectionString=mongodb+srv://...
PaymentDbSettings__DatabaseName=PaymentDb
# JWT dùng chung Secret giữa tất cả Microservices
JwtSettings__Secret=Chuoi_Khoa_Bi_Mat_Cuc_Ky_Dai_It_Nhat_32_Ky_Tu_Random_123456!
JwtSettings__Issuer=AuthService
JwtSettings__Audience=AuthServiceUsers
# PaymentService - VietQR & SePay & Internal Saga Security
OrderService__BaseUrl=http://localhost:5267
OrderService__InternalApiKey=internal_adidas_saga_secret_2026
InternalApi__Key=internal_adidas_saga_secret_2026
VietQrSettings__BankId=MB
VietQrSettings__AccountNo=0987654321
VietQrSettings__AccountName=LE TUNG DUONG
VietQrSettings__Template=compact2
SePaySettings__ApiKey=sepay_adidas_secret_2026
```

---

## 4. NHẬT KÝ ĐIỀU CHỈNH GẦN NHẤT (CHANGELOG)
### 🛡️ Security Patch & CI/CD Release (28/10): Vá 5 Lỗ Hổng Bảo Mật `PaymentService`, `OrderService` & `ApiGateway`
1. **Zero-Trust Pricing Verification (`PaymentProcessingService` & `OrderCallbackClient`):** Chặn tấn công sửa giá (`Amount Tampering`) khi tạo QR hoặc chọn COD bằng cách gọi ngược sang `GET /api/orders/{orderId}` của `OrderService` để lấy `TotalAmount` và `OrderCode` chuẩn xác.
2. **Chống IDOR (`PaymentsController` & `PaymentProcessingService`):** Bắt buộc kiểm tra `UserId` sở hữu đơn hàng trên `POST /api/payments/create-qr`, `POST /api/payments/process-cod` và `GET /api/payments/order/{orderId}`.
3. **Fail-Closed Webhook & Internal API Key:** Bắt buộc cấu hình `SePaySettings:ApiKey` (trả `503` nếu để trống), chỉ nhận header `Apikey <key>` hoặc `X-SePay-Api-Key` (loại bỏ nhầm lẫn với `Bearer`), và bật xác thực `X-Internal-Api-Key` giữa `PaymentService` -> `OrderService`.
4. **Rate Limiting Chuyên Biệt Cho Webhook:** Giới hạn **20 req/phút/IP** (`QueueLimit = 0`) cho `/api/payments/webhook/sepay` tại cả `ApiGateway` và `PaymentService`.
5. **Xác Thực Số Tài Khoản Thụ Hưởng (`HandleSePayWebhookAsync`):** Chặn đứng các gói tin Webhook có `accountNumber` không khớp với `VietQrSettings:AccountNo`.
6. **Container hóa & GitHub Actions CI:** Hoàn thiện `Dockerfile`, `.dockerignore`, `docker-compose.yml`, và `.github/workflows/ci.yml` (kèm service container `mongo:7.0`) cho toàn bộ 5 dịch vụ (`AuthService`, `CatalogService`, `OrderService`, `PaymentService`, `ApiGateway`).
### 🚀 Sprint 2 – Milestone 3 (25/10): `OrderService` Saga Engine OCC + Cancel + Payment Callback
1. Thêm `OrderSagaOrchestrator` (chuyển trạng thái nguyên tử theo Version), `OrderQueryService`, ma trận `SagaTransitions`, `PaymentCallbackPolicy`, `OrderAccessPolicy`.
2. Thêm 5 API `/api/orders` (my-orders, list cho staff, chi tiết, timeline, cancel) và API nội bộ `/api/orders/internal/payment-callback` (header `X-Internal-Api-Key`).
3. Hủy đơn: nhả kho Catalog, lỗi thì ghi outbox `OrderCompensations`. Late payment → `LatePaymentDetected`, không confirm kho.
4. Sửa lỗi culture `vi-VN` của `RangeAttribute` decimal. Tests: `OrderService.Tests` 173/173, `OrderService.Models.Tests` 12/12.
5. Không thay đổi AuthService và CatalogService.

### 🚀 Sprint 1: Nâng cấp `AuthService` (RBAC 3PL) & `CatalogService` (Atomic Stock Reservation Engine):
1. **Mở rộng Phân quyền Chuỗi Cung ứng (`AuthService`):**
   - Chuẩn hóa lớp `AppRoles` gồm 4 vai trò: `Customer`, `Admin`, `LogisticsStaff`, `Driver`.
   - Bổ sung `GET /api/auth/users`, `PATCH /api/auth/users/{id}/role` (tự động thu hồi Refresh Token cũ khi đổi quyền), và `POST /api/auth/users/provision-staff` giúp Admin cấp phát ngay tài khoản `LogisticsStaff` và `Driver` đã xác thực để test toàn trình.
2. **Động cơ Khóa Tồn kho Nguyên tử & Chống Bán lố (`CatalogService`):**
   - Mở rộng `SizeStock` thêm `ReservedQuantity` và `ShoeProduct.TotalReservedStock`.
   - Tạo Collection `StockReservations` với Unique Index `ux_orderId` (đảm bảo tính Lũy đẳng - Idempotency) và Compound Index `ix_status_expiresAt`.
   - Triển khai 3 API Saga nguyên tử (`reserve`, `confirm`, `release`) dùng `FindOneAndUpdateAsync` kèm điều kiện `ElemMatch(s.Size == size && s.Quantity >= qty)` và cơ chế **All-or-Nothing Rollback** khi giỏ hàng có nhiều đôi.
   - Bổ sung `StockReservationExpirationWorker` (`BackgroundService`) tự động quét mỗi 30 giây để thu hồi tồn kho của các phiên giữ hàng quá hạn 15 phút (`Self-Healing TTL Guard`).

### 🛠️ Bản cập nhật CatalogService trước đó (Vòng đời sản phẩm & Quản trị Admin):
1. **Cấu hình Trạng thái hiển thị (`IsActive`):**
   - Bổ sung `bool IsActive = true` mặc định vào `CreateProductRequest` và `UpdateProductRequest`.
2. **Quản lý Xóa 2 cấp độ (Soft Delete vs Hard Delete) & Khôi phục (`Restore`):**
   - Hỗ trợ xóa mềm (`DELETE` mặc định), xóa cứng (`?hardDelete=true`), khôi phục (`PATCH /restore`), và xem sản phẩm ẩn (`?includeInactive=true`).

