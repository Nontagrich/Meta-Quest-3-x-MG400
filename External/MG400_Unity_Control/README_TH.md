# ย้ายชุดควบคุมแขน DOBOT MG400 เข้าโปรเจกต์ Unity ของคุณ

ชุดนี้ใช้ซีนของโปรเจกต์ปลายทาง ตัวควบคุม Unity เชื่อม TCP ไปยัง Python Bridge ซึ่งส่งคำสั่งต่อไปยัง MG400

## 1. ติดตั้งใน Unity

1. เปิดโปรเจกต์ปลายทาง แล้วคัดลอกโฟลเดอร์ `Unity_Assets/MG400Control` เข้าไปใน `Assets` พร้อมไฟล์ `.meta` ถ้ามีคลาสชื่อเดียวกันใน namespace `CPE494` อยู่แล้ว ให้รวมการแก้ไขหรืออัปเดตไฟล์ชุดเดิมก่อน เพื่อไม่ให้ประกาศคลาสซ้ำ
2. โปรเจกต์ต้นทางใช้ Unity **6000.6.5f1**, Input System **1.20.0**, XR Interaction Toolkit **3.6.1** และ Unity UI **2.6.0** สำหรับ TMP ให้ตรวจว่าปลายทางมีแพ็กเกจเหล่านี้หรือรุ่นที่เข้ากันได้ เปิด **Active Input Handling** เป็น Input System Package (New) หรือ Both
3. ในซีนของคุณ สร้าง GameObject เช่น `MG400_Root` แล้วใส่ `MG400BridgeClient`, `MG400Controller`, `MG400DigitalTwin` และ `RobotSafetyMonitor` อย่างละหนึ่งตัว ตั้ง Bridge Client ให้ `Host = 127.0.0.1` และ `Control Port = 5555` เมื่อ Unity กับ Bridge อยู่บน PC เดียวกัน
4. หากต้องการให้โมเดลใน Unity ขยับตามแขน ให้ผูก Transform ของข้อต่อ J1–J4 และปลายแขนใน `MG400DigitalTwin` หากต้องการใช้ Panel อย่างเดียว ช่องข้อต่อของ Twin ปล่อยว่างได้ แต่ยังต้องมีคอมโพเนนต์ Twin
5. ถ้าจะใช้โหมด controller ให้สร้าง GameObject สำหรับ `TeleopStation` แล้วกำหนด `Pad Center` เป็น Transform ของจุดนี้ ตั้ง `Hand Source` ให้ชี้ไปที่ Right Controller ใน XR Origin ซึ่งมี `Tracked Pose Driver` และเปิด Input Actions ของ Position กับ Tracking State อยู่ ใช้ XR Interaction Simulator สำหรับทดสอบบน PC ได้จาก Samples ของ XRI

โฟลเดอร์ `Compatibility` เป็นสคริปต์ที่ `TeleopStation` อ้างถึงเพื่อให้ชุดต้นฉบับคอมไพล์ ไม่ต้องวางคอมโพเนนต์เหล่านี้ในซีนเพื่อใช้ Panel หรือโหมด T

## 2. เปิด Bridge

ใช้ Windows กับ Python 3.11 เปิด PowerShell ในโฟลเดอร์ `Python_Automation` หากต้องการเริ่มแบบจำลองก่อน:

```powershell
py -3 bridge_server.py --sim --no-audio --no-mqtt
```

โหมดจำลองใช้ดูการเชื่อมต่อและ digital twin ได้ ปุ่ม jog ใน Panel ถูกออกแบบให้ใช้เมื่อเป็น Bridge จริง

สำหรับแขนจริง ตั้ง MG400 เป็น TCP/IP และตรวจว่าคอมพิวเตอร์อยู่เครือข่ายเดียวกับแขน ตัวเปิดใช้ IP `192.168.1.6` เป็นค่าเริ่มต้น หาก IP ตรงกัน ให้ออกจาก Unity Play ปิด Bridge จำลองที่เปิดไว้ แล้วดับเบิลคลิก `Start-MG400Bridge.cmd` หาก IP ต่างออกไป ให้ระบุจาก PowerShell เช่น:

```powershell
.\Start-MG400Bridge.cmd -RobotIp 192.168.2.6
```

เปิดหน้าต่าง Bridge ค้างไว้จนเห็น `robot backend: REAL` แล้วกลับไปกด Play ใน Unity ตัวเปิดจะตรวจพอร์ต 5555/5556 และแทนที่ Bridge เก่าที่เปิดจากไฟล์ในโฟลเดอร์เดียวกัน หากเจอโปรแกรมอื่นถือพอร์ตจะบอก PID ให้ตรวจสอบ Bridge ต่อ MG400 ด้วยพอร์ต 29999 และ 30003

## 3. ใช้ Panel และ controller

- เปิดแท็บ Game ใน Unity Editor เพื่อดู Panel `MG400 TEST` เลือก 1, 5 หรือ 10 มม. แล้วใช้ปุ่ม X/Y/Z เมื่อสถานะพร้อม
- คลิก Game แล้วกด **T** เพื่อเริ่มโหมดติดตามมือ หากปลายแขนอยู่นอกกรอบ ระบบจะเริ่มเดินเข้ากรอบเมื่อพบทางที่ผ่านการตรวจ จากนั้นรอให้ขึ้น `CONTROLLER ACTIVE` ก่อนขยับมือ กด T อีกครั้งเพื่อหยุดส่งคำสั่งต่อไป
- ถ้าใช้ XR Device Simulator ที่ตั้งปุ่มเหมือนชุดต้นทาง เลือก Right Controller ด้วย **Y** จากนั้น **W/S** ส่งการเลื่อนหน้า–หลังไปแกน X และ **A/D** ส่งซ้าย–ขวาไปแกน Y หาก Panel ขึ้น `XY EDGE` ให้ขยับกลับเข้าด้านใน
- Panel เป็นส่วนของ Unity Editor ภายใต้ `UNITY_EDITOR` หากต้องการปุ่มในเกมที่ build แล้ว ให้ผูก UI ของโปรเจกต์คุณกับ API ใน `MG400Controller`

## 4. สิ่งที่ควรปรับให้ตรงกับงานใหม่

- กรอบ XY อยู่ใน `Python_Automation/motion_workspace.py` ที่ `CORNERS` เรียงรอบพื้นที่ 1 → 2 → 4 → 3 ตัวตรวจยังใช้เกณฑ์รัศมี 220–440 มม. จากโปรเจกต์ต้นทาง ทั้งสี่จุดเป็นพิกัดอ้างอิงของเครื่องเดิม หากตำแหน่งฐาน ชิ้นงาน หรือระบบพิกัดของแขนเปลี่ยน ให้กำหนดจุดใหม่ก่อนใช้เครื่องจริง กรอบนี้ใช้กับตำแหน่งปลายแขนบน XY
- โหมด T ใช้ `SpeedFactor(20)` คง Z ตอนเริ่ม และจำกัด R ที่ −90° ถึง −55° ค่าอยู่ใน `planar_teleop.py`
- ถ้าเรียกจากโค้ดของโปรเจกต์ใหม่ ให้ใช้ `MG400Controller.MoveCartesianMm(x, y, z, r)` เป็น coroutine หน่วย X/Y/Z คือมิลลิเมตร และ R เป็นองศา Bridge จะตรวจตำแหน่งปัจจุบันและเส้นทาง XY ก่อนส่งคำสั่ง
- Workspace ส่วนของงานแพ็กสามารถตรวจล่วงหน้าด้วย `MG400Controller.CheckPackingWorkspace(...)` เมื่อผูกพิกัดของงานใหม่แล้ว

การตรวจ ZIP ยืนยันชุดไฟล์และความถูกต้องของ archive การทดสอบในโปรเจกต์ปลายทางและกับแขนจริงต้องทำหลังเชื่อมตั้งค่าในซีนของคุณ

[ภาพพิกัดกรอบ XY ชุดเดิม](Docs/MG400_Workspace_XY.png)
