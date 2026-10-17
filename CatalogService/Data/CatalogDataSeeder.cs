using CatalogService.Models;
using MongoDB.Driver;

namespace CatalogService.Data;

public static class CatalogDataSeeder
{
    public static async Task SeedAsync(IMongoCollection<ShoeProduct> collection)
    {
        var count = await collection.CountDocumentsAsync(_ => true);
        if (count > 0) return; // Đã có dữ liệu, không seed đè

        var sampleShoes = new List<ShoeProduct>
        {
            new ShoeProduct
            {
                Name = "Adidas Ultraboost Light Unisex",
                Category = "Running",
                Gender = "Unisex",
                Description = "Đôi giày chạy bộ nhẹ nhất từ trước đến nay của Adidas với đệm Light BOOST thế hệ mới. Đem lại lực hoàn trả năng lượng tối đa trên từng bước chạy.",
                Price = 4500000,
                SalePrice = 3800000,
                Color = "Core Black / Cloud White",
                ThumbnailUrl = "https://assets.adidas.com/images/h_840,f_auto,q_auto,fl_lossy,c_fill,g_auto/7c490a6f44e24ef5a2e6af5b00f89839_9366/Giay_Ultraboost_Light_trang_GY9350_01_standard.jpg",
                ImageUrls = new List<string>
                {
                    "https://assets.adidas.com/images/h_840,f_auto,q_auto,fl_lossy,c_fill,g_auto/7c490a6f44e24ef5a2e6af5b00f89839_9366/Giay_Ultraboost_Light_trang_GY9350_01_standard.jpg",
                    "https://assets.adidas.com/images/h_840,f_auto,q_auto,fl_lossy,c_fill,g_auto/a3b34b150c264a9fb6f8af5b00f8a846_9366/Giay_Ultraboost_Light_trang_GY9350_02_standard.jpg",
                    "https://assets.adidas.com/images/h_840,f_auto,q_auto,fl_lossy,c_fill,g_auto/6a88b566c30349b1a5e1af5b00f8b1a2_9366/Giay_Ultraboost_Light_trang_GY9350_04_standard.jpg"
                },
                Sizes = new List<SizeStock>
                {
                    new() { Size = 39, Quantity = 8 },
                    new() { Size = 40, Quantity = 15 },
                    new() { Size = 41, Quantity = 20 },
                    new() { Size = 42, Quantity = 12 },
                    new() { Size = 43, Quantity = 5 }
                },
                Tags = new List<string> { "Best Seller", "New Arrival" },
                AverageRating = 4.9,
                ReviewCount = 3,
                Reviews = new List<ShoeReview>
                {
                    new() { UserId = "user1", FullName = "Nguyễn Văn Hùng", Rating = 5, Comment = "Đế Boost nhẹ và êm tuyệt vời, chạy 10km không thấy mỏi.", CreatedAt = DateTime.UtcNow.AddDays(-5) },
                    new() { UserId = "user2", FullName = "Trần Thị Mai", Rating = 5, Comment = "Form chuẩn, màu sắc phối rất sang.", CreatedAt = DateTime.UtcNow.AddDays(-3) },
                    new() { UserId = "user3", FullName = "Lê Hoàng", Rating = 5, Comment = "Đáng đồng tiền bát gạo, giao hàng nhanh.", CreatedAt = DateTime.UtcNow.AddDays(-1) }
                }
            },
            new ShoeProduct
            {
                Name = "Adidas Samba OG Unisex",
                Category = "Originals",
                Gender = "Unisex",
                Description = "Biểu tượng thời trang đường phố từ sân cỏ thập niên 50. Thân giày bằng da cao cấp phối da lộn ở mũi chữ T và đế cao su gum kinh điển.",
                Price = 2800000,
                SalePrice = null,
                Color = "Cloud White / Core Black",
                ThumbnailUrl = "https://assets.adidas.com/images/h_840,f_auto,q_auto,fl_lossy,c_fill,g_auto/3bbecbdf584e40518210acb600d75a06_9366/Giay_Samba_OG_trang_B75806_01_00_standard.jpg",
                ImageUrls = new List<string>
                {
                    "https://assets.adidas.com/images/h_840,f_auto,q_auto,fl_lossy,c_fill,g_auto/3bbecbdf584e40518210acb600d75a06_9366/Giay_Samba_OG_trang_B75806_01_00_standard.jpg",
                    "https://assets.adidas.com/images/h_840,f_auto,q_auto,fl_lossy,c_fill,g_auto/b58535a9632348cc8185acb600d7681d_9366/Giay_Samba_OG_trang_B75806_02_standard_hover.jpg"
                },
                Sizes = new List<SizeStock>
                {
                    new() { Size = 38, Quantity = 6 },
                    new() { Size = 39, Quantity = 14 },
                    new() { Size = 40, Quantity = 25 },
                    new() { Size = 41, Quantity = 18 },
                    new() { Size = 42, Quantity = 10 },
                    new() { Size = 43, Quantity = 4 }
                },
                Tags = new List<string> { "Hot Sale", "Trending", "Best Seller" },
                AverageRating = 4.8,
                ReviewCount = 2,
                Reviews = new List<ShoeReview>
                {
                    new() { UserId = "user4", FullName = "Phạm Quỳnh Anh", Rating = 5, Comment = "Đôi giày quốc dân phối đồ gì cũng đẹp!", CreatedAt = DateTime.UtcNow.AddDays(-4) },
                    new() { UserId = "user5", FullName = "Đặng Tiến Dũng", Rating = 4, Comment = "Giày rất đẹp, mang vài hôm da sẽ mềm hơn.", CreatedAt = DateTime.UtcNow.AddDays(-2) }
                }
            },
            new ShoeProduct
            {
                Name = "Adidas Stan Smith Classic",
                Category = "Originals",
                Gender = "Unisex",
                Description = "Mẫu giày tennis vượt thời gian với phối màu gót xanh lá huyền thoại. Thân giày da thuần chay thân thiện môi trường, thiết kế tối giản tinh tế.",
                Price = 2400000,
                SalePrice = 1990000,
                Color = "Cloud White / Green",
                ThumbnailUrl = "https://assets.adidas.com/images/h_840,f_auto,q_auto,fl_lossy,c_fill,g_auto/f47d4e5f4d1e4e1a8bb2ad3200b212f8_9366/Giay_Stan_Smith_trang_FX5502_01_standard.jpg",
                ImageUrls = new List<string>
                {
                    "https://assets.adidas.com/images/h_840,f_auto,q_auto,fl_lossy,c_fill,g_auto/f47d4e5f4d1e4e1a8bb2ad3200b212f8_9366/Giay_Stan_Smith_trang_FX5502_01_standard.jpg"
                },
                Sizes = new List<SizeStock>
                {
                    new() { Size = 38, Quantity = 10 },
                    new() { Size = 39, Quantity = 12 },
                    new() { Size = 40, Quantity = 16 },
                    new() { Size = 41, Quantity = 15 },
                    new() { Size = 42, Quantity = 8 }
                },
                Tags = new List<string> { "Best Seller" },
                AverageRating = 4.7,
                ReviewCount = 1,
                Reviews = new List<ShoeReview>
                {
                    new() { UserId = "user6", FullName = "Hoàng Hải", Rating = 5, Comment = "Mẫu kinh điển, đi học đi làm đều hợp.", CreatedAt = DateTime.UtcNow.AddDays(-6) }
                }
            },
            new ShoeProduct
            {
                Name = "Adidas Predator Elite FT FG",
                Category = "Football",
                Gender = "Unisex",
                Description = "Vũ khí săn bàn tối thượng trên sân cỏ tự nhiên. Công nghệ gai Strikeskin và lưỡi gà gập cổ điển giúp kiểm soát bóng hoàn hảo.",
                Price = 6500000,
                SalePrice = 5850000,
                Color = "Lucid Blue / Solar Yellow",
                ThumbnailUrl = "https://assets.adidas.com/images/h_840,f_auto,q_auto,fl_lossy,c_fill,g_auto/8df0bf17b70742f58e576be896f53a1f_9366/Giay_Da_Bong_San_Co_Tu_Nhien_Predator_Elite_FT_FG_trang_IG7730_01_standard.jpg",
                ImageUrls = new List<string>
                {
                    "https://assets.adidas.com/images/h_840,f_auto,q_auto,fl_lossy,c_fill,g_auto/8df0bf17b70742f58e576be896f53a1f_9366/Giay_Da_Bong_San_Co_Tu_Nhien_Predator_Elite_FT_FG_trang_IG7730_01_standard.jpg"
                },
                Sizes = new List<SizeStock>
                {
                    new() { Size = 40, Quantity = 5 },
                    new() { Size = 41, Quantity = 7 },
                    new() { Size = 42, Quantity = 6 },
                    new() { Size = 43, Quantity = 3 }
                },
                Tags = new List<string> { "New Arrival", "Hot Sale" },
                AverageRating = 5.0,
                ReviewCount = 1,
                Reviews = new List<ShoeReview>
                {
                    new() { UserId = "user7", FullName = "Vũ Quang", Rating = 5, Comment = "Đá bóng cực sướng, sút bóng xoáy chuẩn xác.", CreatedAt = DateTime.UtcNow.AddDays(-2) }
                }
            },
            new ShoeProduct
            {
                Name = "Adidas Forum Low CL",
                Category = "Basketball",
                Gender = "Unisex",
                Description = "Đôi giày bóng rổ biểu tượng từ năm 1984, được tái thiết kế cho phong cách đường phố đương đại. Chi tiết da cao cấp với đường gân chữ X trứ danh.",
                Price = 2600000,
                SalePrice = 2200000,
                Color = "Cloud White / Off White",
                ThumbnailUrl = "https://assets.adidas.com/images/h_840,f_auto,q_auto,fl_lossy,c_fill,g_auto/01f463cfa2a54366b2a0ae8700ef550f_9366/Giay_Forum_Low_CL_trang_HQ7098_01_standard.jpg",
                ImageUrls = new List<string>
                {
                    "https://assets.adidas.com/images/h_840,f_auto,q_auto,fl_lossy,c_fill,g_auto/01f463cfa2a54366b2a0ae8700ef550f_9366/Giay_Forum_Low_CL_trang_HQ7098_01_standard.jpg"
                },
                Sizes = new List<SizeStock>
                {
                    new() { Size = 39, Quantity = 8 },
                    new() { Size = 40, Quantity = 14 },
                    new() { Size = 41, Quantity = 18 },
                    new() { Size = 42, Quantity = 9 }
                },
                Tags = new List<string> { "Hot Sale", "Trending" },
                AverageRating = 4.8,
                ReviewCount = 1,
                Reviews = new List<ShoeReview>
                {
                    new() { UserId = "user8", FullName = "Bảo Long", Rating = 5, Comment = "Đôi này đi êm và phong cách vintage cực chất.", CreatedAt = DateTime.UtcNow.AddDays(-1) }
                }
            },
            new ShoeProduct
            {
                Name = "Adidas Dropset 2 Trainer",
                Category = "Training",
                Gender = "Unisex",
                Description = "Chuyên biệt cho tập Gym và rèn luyện thể lực nặng. Đế giữa mật độ kép mang lại độ vững chãi tối đa khi nâng tạ (Squat, Deadlift).",
                Price = 3300000,
                SalePrice = null,
                Color = "Lucid Lemon / Core Black",
                ThumbnailUrl = "https://assets.adidas.com/images/h_840,f_auto,q_auto,fl_lossy,c_fill,g_auto/fbb11ad9ad0a4bb6a3fbafbb00f13a01_9366/Giay_Tap_Luyen_Dropset_2_vang_ID4975_01_standard.jpg",
                ImageUrls = new List<string>
                {
                    "https://assets.adidas.com/images/h_840,f_auto,q_auto,fl_lossy,c_fill,g_auto/fbb11ad9ad0a4bb6a3fbafbb00f13a01_9366/Giay_Tap_Luyen_Dropset_2_vang_ID4975_01_standard.jpg"
                },
                Sizes = new List<SizeStock>
                {
                    new() { Size = 40, Quantity = 10 },
                    new() { Size = 41, Quantity = 15 },
                    new() { Size = 42, Quantity = 12 },
                    new() { Size = 43, Quantity = 6 }
                },
                Tags = new List<string> { "New Arrival" },
                AverageRating = 5.0,
                ReviewCount = 1,
                Reviews = new List<ShoeReview>
                {
                    new() { UserId = "user9", FullName = "Lê Minh Trí", Rating = 5, Comment = "Trụ chân siêu chắc, tập tạ nặng rất yên tâm.", CreatedAt = DateTime.UtcNow.AddDays(-3) }
                }
            }
        };

        await collection.InsertManyAsync(sampleShoes);
    }
}
