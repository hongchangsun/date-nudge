# -*- coding: utf-8 -*-
import re

filepath = r"C:\Users\Administrator\Desktop\日期提醒_解压_3接口\MainForm.cs"
with open(filepath, "r", encoding="utf-8") as f:
    lines = f.readlines()

new_lines = []
i = 0
while i < len(lines):
    line = lines[i]
    new_lines.append(line)
    
    # A. ProcessRawInput 入口日志 (在 "uint size = 0;" 前插入)
    if "uint size = 0;" in line and "_usbHidCharBuffer" not in line:
        indent = "            "
        new_lines.append(f'{indent}Logger.Info($"[RAW] ProcessRawInput called");\n')
        print(f"A: Line {i+1} - RAW entry log")
    
    # B. Enter 触发时记录 barcode
    if "string barcode = _usbHidCharBuffer;" in line:
        indent = "            "
        new_lines.append(f'{indent}Logger.Info($"[RAW] Enter fired barcode=[{{barcode}}] waiting={{_usbHidWaitingActivation}} boundHandle={{_usbHidDeviceHandle}}");\n')
        print(f"B: Line {i+1} - Enter/barcode log")
    
    # C. 设备匹配判断前
    if "_usbHidDeviceHandle != IntPtr.Zero && deviceHandle == _usbHidDeviceHandle)" in line and "if (" in lines[i-1] if i > 0 else False:
        indent = "                "
        new_lines.insert(-1, f'{indent}Logger.Info($"[RAW] Device check: bound={{_usbHidDeviceHandle}} current={{deviceHandle}} match={{(_usbHidDeviceHandle != IntPtr.Zero && deviceHandle == _usbHidDeviceHandle)}}");\n')
        print(f"C: Line {i+1} - Device match log")
    
    # D. ProcessScanData 入口
    if "// 密码绑定：扫描 mima:XXXX 格式的二维码" in line:
        indent = "            "
        new_lines.append(f'{indent}Logger.Info($"[SCAN] ProcessScanData rawData=[{{rawData}}] isStarted={{_isStarted}} password=[{{_cfg.ScannerPassword ?? \"(none)\"}}]");\n')
        print(f"D: Line {i+1} - SCAN entry log")
    
    # E. 正则匹配结果
    if 'var match = Regex.Match(rawData' in line:
        indent = "            "
        # 在下一行（if 判断）之前插入
        print(f"E: Line {i+1} - regex log (will insert after)")
    
    # F. 解密结果
    if "string decryptedNum = BigDecrypt" in line:
        indent = "                "
        new_lines.append(f'{indent}Logger.Info($"[SCAN] Decrypt: encrypted=[{{encryptedNum}}] decrypted=[{{decryptedNum}}]");\n')
        print(f"F: Line {i+1} - Decrypt log")
    
    # G. 最终输出
    if "模拟键盘输入:" in line:
        indent = "            "
        new_lines.append(f'{indent}Logger.Info($"[SCAN] OUTPUT: output=[{{output}}] isStarted={{_isStarted}} target=[{{_cfg.SoftwareName ?? \"(none)\"}}] mode={{_cfg.OutputMode}}");\n')
        print(f"G: Line {i+1} - Final output log")
    
    # H. 未启动丢弃
    if "未点" in line and "启动" in line and "不处理" in line:
        indent = "                "
        new_lines.append(f'{indent}Logger.Warn($"[SCAN] DISCARDED! Not started. rawData=[{{rawData}}]");\n')
        print(f"H: Line {i+1} - Discarded log")

    i += 1

# E 需要特殊处理：在正则匹配行之后、if 之前插入
final_lines = []
for i, line in enumerate(new_lines):
    final_lines.append(line)
    if 'var match = Regex.Match(rawData' in line:
        indent = "            "
        final_lines.append(f'{indent}Logger.Info($"[SCAN] regex Success={{match.Success}} g1=[{{match.Groups[1].Value}}] g2=[{{match.Groups[2].Value}}]");\n')
        print(f"E (post): inserted after line {i+1}")

with open(filepath, "w", encoding="utf-8") as f:
    f.writelines(final_lines)

print("\nDone! All logs added.")
