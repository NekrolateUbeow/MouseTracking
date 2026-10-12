MouseTracking - tài liệu sử dụng
==============================

Giới thiệu
----------
MouseTracking kết nối với VTube Studio qua Public API và cập nhật hai tham số
MouseLookX, MouseLookY để điều khiển hướng nhìn của model.

Ở chế độ con trỏ, hướng nhìn được tính từ khoảng cách giữa con trỏ và vị trí
ước tính của đầu model. Ở chế độ FPS, chương trình dùng chuyển động chuột thô
(Raw Input) để cập nhật góc nhìn khi con trỏ bị ẩn hoặc khóa.

Các tệp
-------
- Program.cs: khởi động, đọc tham số dòng lệnh, quản lý kết nối và cập nhật tham số.
- RelativeLook.cs: đọc Raw Input, nhận diện chế độ FPS và xử lý góc nhìn/decay.
- VTubeStudioClient.cs: kết nối WebSocket và trao đổi thông điệp với VTube Studio.
- WindowsApi.cs: khai báo Windows API và lấy thông tin màn hình.
- MouseLookAt.csproj: cấu hình dự án .NET.

Yêu cầu
------
- Windows.
- VTube Studio đang chạy và bật Public API trên cổng được cấu hình.
- .NET SDK 8 để biên dịch mã nguồn.

Cách chạy
---------
Chạy MouseLookAt.exe. Lần đầu kết nối, VTube Studio có thể hiện hộp thoại yêu
cầu cấp quyền cho plugin. Chấp thuận yêu cầu để chương trình lưu token vào
vts_token.txt cạnh tệp thực thi và dùng lại ở lần chạy sau.

Có thể truyền tùy chọn theo dạng --ten=giatri. Tên tùy chọn không phân biệt
chữ hoa/chữ thường. Tùy chọn dạng cờ như --invy hoặc --debug được nhận diện
chỉ dựa trên việc có xuất hiện; --debug hiện không bật log vì phần log kiểm thử
đang được giữ dưới dạng comment trong mã nguồn.

Tùy chọn chung
--------------
--port=8001
    Cổng Public API của VTube Studio. Mặc định: 8001.

--hz=30
    Tần suất cập nhật chuột và tham số, tính theo lần mỗi giây. Mặc định: 30.

--keepalive=200
    Khoảng thời gian tối đa giữa các lần gửi khi giá trị không đổi, tính bằng
    mili giây. Giúp duy trì quyền điều khiển tham số. Mặc định: 200.

--r=450
    Bán kính tính hướng nhìn ở chế độ con trỏ, tính bằng pixel. Nếu không chỉ
    định, chương trình dùng 0.45 lần chiều cao vùng màn hình tham chiếu.

--screen=x,y,w,h
    Vùng màn hình tham chiếu, gồm tọa độ góc trên bên trái x,y và chiều rộng,
    chiều cao w,h, tính bằng pixel. Mặc định là màn hình chính với gốc tọa độ
    (0,0). Dùng dấu chấm cho phần thập phân nếu cần.

--headx=0.03
    Độ lệch ngang của tâm đầu so với tâm model, theo hệ tọa độ chuẩn hóa. Mặc
    định: 0.03.

--heady=0.25
    Độ lệch dọc của tâm đầu so với tâm model, theo hệ tọa độ chuẩn hóa. Mặc
    định: 0.25.

--rotsign=1
    Chiều quy đổi góc xoay của model. Giá trị mặc định 1; dùng -1 để đảo chiều.

Tùy chọn chế độ FPS
-------------------
--mode=auto|fps|cursor
    Chọn chế độ chuột. auto tự nhận diện; fps luôn dùng Raw Input; cursor luôn
    dùng vị trí con trỏ. Mặc định: auto.

--sens=1.0
    Hệ số nhạy bổ sung cho chuyển động chuột thô. Mặc định: 1.0.

--yaw=0.05
    Hệ số góc ngang theo mỗi count chuột, trước khi nhân độ nhạy. Mặc định: 0.022.

--pitch=0.05
    Hệ số góc dọc theo mỗi count chuột, trước khi nhân độ nhạy. Mặc định: 0.022.

--yawrange=90
    Góc ngang tối đa tương ứng với đầu ra -1 hoặc 1. Mặc định: 90 độ.

--pitchrange=60
    Góc dọc tối đa tương ứng với đầu ra -1 hoặc 1. Mặc định: 60 độ.

--invy
    Đảo chiều đầu ra theo trục Y.

--decaydelay=0.25
    Thời gian chờ sau lần di chuyển chuột cuối trước khi bắt đầu đưa hướng nhìn
    về tâm. Mặc định: 0.25 giây.

--decaytime=2.0
    Thời gian giảm hướng nhìn từ giá trị cực đại về ngưỡng snap. Mặc định:
    2.0 giây. Giá trị 0 tắt decay.

--decaysnap=0.05
    Khi trị tuyệt đối của đầu ra nhỏ hơn ngưỡng này, đầu ra được đặt về 0.
    Mặc định: 0.05.

--requirehidden=false
    Cho phép nhận diện FPS ngay cả khi con trỏ không bị ẩn. Mặc định là yêu
    cầu con trỏ ẩn; chỉ giá trị false mới tắt yêu cầu này.

--lockpx=8
    Ngưỡng dịch chuyển con trỏ tối đa trong cửa sổ nhận diện để xem là bị khóa.
    Mặc định: 8 pixel.

--exitpx=30
    Độ dịch chuyển ròng của con trỏ để thoát chế độ FPS. Mặc định: 30 pixel.

--entercounts=120
    Tổng chuyển động Raw Input cần có trong cửa sổ nhận diện để vào chế độ FPS.
    Mặc định: 120 count.

Ví dụ (dùng khi gọi MouseTracking.exe bằng start_without_steam.bat)
-----
@echo off
start "" "%~dp0VTube Studio.exe" -nosteam

:WAIT
powershell -NoProfile -Command "if (Test-NetConnection 127.0.0.1 -Port 8001 -InformationLevel Quiet) { exit 0 } else { exit 1 }"

if errorlevel 1 (
    timeout /t 1 /nobreak >nul
    goto WAIT
)

start "" /b "%~dp0MouseTracking.exe" --mode=auto --yawrange=90 --pitchrange=60 --sens=0.75

exit

Lưu ý
-----
- Các giá trị số dùng dấu chấm làm dấu thập phân.
- --r chỉ được áp dụng khi có truyền tham số này; nếu không, bán kính được tính
  theo chiều cao màn hình.
- --screen chỉ được chấp nhận khi có đủ bốn giá trị và chiều rộng, chiều cao
  lớn hơn 0. Nếu sai định dạng, chương trình tiếp tục dùng màn hình chính.
- Tham số MouseLookX và MouseLookY được tạo nếu chưa có. Nếu chúng đã tồn tại,
  lỗi tạo tham số có thể được bỏ qua.
- Chương trình tự thử kết nối lại sau khi xảy ra lỗi kết nối.
- Khi dùng Raw Input, cấu trúc dữ liệu hiện được đọc theo layout Windows x64.