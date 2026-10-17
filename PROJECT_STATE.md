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
- **Phân quyền Role:** Claim `ClaimTypes.Role` chứa `"Customer"` (mặc định) hoặc `"Admin"`.

---

### ✅ Service 2: `CatalogService` (Đã hoàn thành 100%)
- **Thư mục:** `/CatalogService`
- **Công nghệ:** .NET 8 Web API, MongoDB Driver, JWT Bearer, Swagger.
- **Port Docker:** `5266:8080` (Cổng local HTTP: `5266`)
- **Database:** MongoDB `CatalogDb` (Collection: `Products`).
- **Chức năng chính đã hoạt động:**
  1. `GET /api/catalog/products`: Tra cứu danh mục, lọc theo `keyword`, `category`, `gender`, `color`, `size` (chỉ lấy size còn hàng), `minPrice`/`maxPrice`, `onSale`, `tag`, sắp xếp (`newest`, `price_asc`, `price_desc`, `rating`, `popular`), phân trang (`Page`, `PageSize`). (Public)
  2. `GET /api/catalog/products/{id}`: Xem chi tiết đôi giày, bảng size tồn kho, album ảnh, đánh giá. (Public)
  3. `GET /api/catalog/filters`: Lấy metadata bộ lọc (dòng giày, màu sắc, size còn hàng, khoảng giá Min/Max). (Public)
  4. `POST /api/catalog/products/{id}/reviews`: Gửi đánh giá sao (1-5) & nhận xét, tự động tính lại `AverageRating` và `ReviewCount`. (Yêu cầu đăng nhập `Customer` hoặc `Admin`)
  5. `POST /api/catalog/products`: Thêm mẫu giày mới vào cửa hàng. (Yêu cầu `Admin`)
  6. `PUT /api/catalog/products/{id}`: Cập nhật thông tin, giá, bảng size tồn kho. (Yêu cầu `Admin`)
  7. `DELETE /api/catalog/products/{id}`: Ẩn sản phẩm khỏi cửa hàng (Xóa mềm). (Yêu cầu `Admin`)
  8. **Tự động Seeder:** Khởi tạo sẵn 6 mẫu giày Adidas thật (Ultraboost Light, Samba OG, Stan Smith, Predator Elite, Forum Low, Dropset 2) kèm ảnh thật, giá và review mẫu khi khởi động.

---

### ⏳ Service 3: `OrderService` (Dự kiến tiếp theo)
- **Mục tiêu:** Giỏ hàng (`Cart`) và Đặt hàng (`Checkout`).

### ⏳ Service 4: `ApiGateway` (Dự kiến chốt hạ)
- **Công nghệ:** YARP (Yet Another Reverse Proxy) .NET 8.
- **Port:** `5000:8080` (Cổng duy nhất cho Web/Mobile client truy cập).

---

## 3. CẤU TRÚC BIẾN MÔI TRƯỜNG CHUNG (`.env`)
```ini
ASPNETCORE_ENVIRONMENT=Development
# Mongo
MongoDbSettings__ConnectionString=mongodb+srv://...
MongoDbSettings__DatabaseName=AuthDb
# CatalogDb dùng chung ConnectionString hoặc định nghĩa riêng
CatalogDbSettings__ConnectionString=mongodb+srv://...
CatalogDbSettings__DatabaseName=CatalogDb
# JWT dùng chung Secret để CatalogService verify token do AuthService cấp
JwtSettings__Secret=Chuoi_Khoa_Bi_Mat_Cuc_Ky_Dai_It_Nhat_32_Ky_Tu_Random_123456!
JwtSettings__Issuer=AuthService
JwtSettings__Audience=AuthServiceUsers
```
