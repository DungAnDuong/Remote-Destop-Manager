# 06 – Thiết kế giao diện (Bước 8)

**Từ phiên bản 0.5.0:** `RDM.exe` mở lên là cửa sổ hai cột (Cho phép điều khiển | Điều khiển máy khác). Mục 1 dưới đây mô tả cửa sổ **Quản lý thiết bị**, mở từ nút trong cửa sổ chính sau khi đăng nhập. Mục 3 dưới đây là cột bên trái của cửa sổ chính.

## 1. Controller

```
+----------------+------------------------------------------------------+
| REMOTE DESKTOP |  Dashboard                        [Dark/Light] admin |
| MANAGER        +------------------------------------------------------+
|                |  +-------+ +-------+ +-------+ +-------+ +-------+   |
| > Dashboard    |  | Total | |Online | |Offline| |Active | |Connec-|   |
|   My Computers |  |Devices| |       | |       | |Session| | tions |   |
|   Groups       |  +-------+ +-------+ +-------+ +-------+ +-------+   |
|   Recent       |  +----------------------+ +-----------------------+ |
|   Favorites    |  | Quick Connect        | | Online / Offline      | |
|   Active Sess. |  +----------------------+ +-----------------------+ |
|   File Transfer|  +----------------------+ +-----------------------+ |
|   History      |  | Lịch sử kết nối      | | Recent Activities     | |
|   Users        |  +----------------------+ +-----------------------+ |
|   Security     |                                                      |
|   Settings     |                                                      |
|   About        |                                                      |
+----------------+------------------------------------------------------+
```

- **Màu:** Sidebar xanh đậm ở cả hai chế độ. Nội dung xám nhạt / trắng (Light) hoặc xanh than (Dark). Toàn bộ màu nằm trong `Theme`, không viết cứng ở Form.
- **Font:** Segoe UI; icon dùng Segoe UI Symbol để có trên Windows 7.
- **Control tự vẽ:** `ModernButton` (bo góc, hover), `Card` (bo góc), `SidebarButton`, `ModernInput`, `StatCard`, `BarChart`, `PresenceBar`, `GridView<T>`.
- **Biểu đồ:** không dùng biểu đồ tròn 2 phần cho Online/Offline; dùng `PresenceBar` kèm nhãn và số. Biểu đồ cột một chuỗi dùng màu `Accent` (đã kiểm tra độ tương phản với nền sáng và tối bằng validator). Màu trạng thái luôn đi kèm ký hiệu và chữ. Số liệu đầy đủ xem ở trang Connection History.
- **DPI:** System DPI Aware, mọi kích thước qua `Dpi.S()`.
- **Kích thước tối thiểu:** 1024x768.
- **Bảng dữ liệu:** `DataGridView` ở `VirtualMode`, tìm kiếm và lọc ở tầng SQL, nên chịu được hàng nghìn thiết bị.

## 2. Nguyên tắc trung thực trên giao diện

Chức năng thuộc giai đoạn chưa làm **không được giả lập**. Trang tương ứng ghi rõ "Chưa triển khai, thuộc Giai đoạn N". Nút CONNECT cũng bị vô hiệu hóa với cùng lý do. Trạng thái thiết bị hiển thị **"Chưa rõ"** cho tới khi có dữ liệu thật từ Relay: ghi "Offline" khi chưa biết cũng là khẳng định sai. Không sinh số liệu mẫu.

## 3. Client (Giai đoạn 3)

```
+-------------------------------------+
| RDM                                 |
|  Remote ID    123 456 789   [Copy]  |
|  Trạng thái   Online                |
|  Máy tính     PC-KT-01              |
|  Hệ điều hành Windows 10 x64        |
|  IP           10.0.0.12             |
|-------------------------------------|
|  Mật khẩu truy cập  [Change Password]|
|  [ ] Enable Unattended Access       |
|  [ ] Disable Remote Access          |
|-------------------------------------|
|  Lịch sử kết nối                    |
|  [Disconnect All]        [Settings] |
+-------------------------------------+
```

Khi có phiên: thanh nổi luôn hiện trên cùng ghi người điều khiển, kèm nút **Ngắt ngay**. Thanh này không thể tắt bằng cấu hình.
