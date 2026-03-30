from PIL import Image
import os

ico_path = "icon.ico"
temp_ico = "temp.ico"

try:
    print("Reading icon.ico which is likely a PNG...")
    img = Image.open(ico_path)
    print("Image format is actually:", img.format)
    # Convert it to a real ICO!
    img.save(temp_ico, format="ICO", sizes=[(256, 256), (128, 128), (64, 64), (32, 32), (16, 16)])
    print("Saved real ICO to temp.ico")
    
    img.close()
    
    os.remove(ico_path)
    os.rename(temp_ico, ico_path)
    print("Successfully replaced icon.ico with a proper ICO file.")
except Exception as e:
    print("Error:", e)
