สร้างไฟล์ CLAUDE.md ที่ root ของโปรเจกต์ Unity นี้ ด้วยเนื้อหานี้:

# Gem Store AR – CPE494 Class Project
## Context
- แอป Mixed Reality ขายอัญมณีบน Meta Quest 3 + ระบบควบคุมแขนกลทางไกล
- Unity 6, URP, Android (Meta Quest build profile)
- XR stack: OpenXR + XR Interaction Toolkit 3.6.1 (Starter Assets) + XR Hands
- ห้ามติดตั้ง Meta XR All-in-One SDK / Oculus Integration (จะชนกับ XRI)
- รองรับทั้ง controller และ hand tracking

## Folder structure
Assets/_Project/{Scenes,Scripts,Prefabs,Materials,Data,Audio,Models}
- Scripts แยก namespace: GemStore.Showcase, GemStore.Payment, GemStore.Network, GemStore.Teleop

## Rules
- ใช้ XRI components (XRGrabInteractable, XRSimpleInteractable, Poke) แทนการเขียน input เอง
- ทุก UI ใช้ World Space Canvas + TextMeshPro, ขนาดอ่านได้ที่ระยะ 0.5–1 m
- คำนึง performance บน Quest: หลีกเลี่ยง real-time shadow หนัก, post-processing, transparent overdraw มาก
- หลังแก้ไขทุกครั้ง ตรวจ Console ว่าไม่มี error และสรุปสิ่งที่เปลี่ยน + สิ่งที่ฉันต้องทำเองใน Editor
- ห้ามลบหรือแก้ไฟล์ใน Assets/Samples