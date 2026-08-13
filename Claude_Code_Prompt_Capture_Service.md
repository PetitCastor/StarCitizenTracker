# Prompt for Claude Code: Star Citizen Mission Capture Service

## Context
You are acting as an expert Python developer. We are building a lightweight desktop tray service for Windows that captures live gameplay of Star Citizen, uses OCR to extract mission contract text from the in-game "mobiGlas" UI, and sends this structured data to a Web API. 
The service must be highly optimized to avoid impacting the game's framerate.

## Technology Stack
*   **Language:** Python 3.11+
*   **Screen Capture:** `dxcam` (High-performance Windows desktop duplication API wrapper for ultra-fast, low-overhead capture).
*   **Image Processing:** `opencv-python` (cv2) and `numpy` (for cropping, grayscale, and thresholding the holographic UI).
*   **OCR Engine:** `pytesseract` (Python wrapper for Tesseract OCR).
*   **Desktop Tray UI:** `pystray` and `Pillow` (for the system tray icon, menu, and state management).
*   **HTTP Client:** `requests` (for POSTing parsed data to the Web API).
*   **Packaging:** `PyInstaller` (to eventually compile into a standalone Windows executable).

## Development Roadmap & Tasks

Please implement this project step-by-step. Do not move to the next phase until the current phase is fully implemented and tested.

### Phase 1: Skeleton & System Tray Setup
1. Initialize the project structure and a `requirements.txt`.
2. Implement a system tray application using `pystray`. The menu should include: "Start Capture", "Pause Capture", "Settings", and "Exit".
3. Implement a configuration manager that reads/writes to a local `config.json`. It needs to store: `api_endpoint`, `auth_token`, `capture_fps` (default 2), and `monitor_index`.
4. Ensure the app runs quietly in the background without blocking the main thread.

### Phase 2: Capture Module Implementation
1. Integrate `dxcam` to capture frames at the configured FPS.
2. Implement bounding box configurations to crop the screen. We need to isolate the right side of the screen where the mobiGlas mission details (Title, Reward, Objectives) appear. Provide standard offsets for a 16:9 1440p monitor as a baseline.
3. Hook the capture loop to the `pystray` state so it only runs when "Start Capture" is active.

### Phase 3: Vision Pre-processing (OpenCV)
*Star Citizen's UI is semi-transparent holographs. Raw OCR will fail.*
1. Build an image processing pipeline function that takes the cropped frame from Phase 2.
2. Convert the image to grayscale (`cv2.cvtColor`).
3. Apply binary thresholding (`cv2.threshold` or `cv2.adaptiveThreshold`) to isolate the light text from the bleeding planetary background, resulting in a stark black-and-white image.
4. Save debug images to disk temporarily during this phase so we can verify the thresholding works.

### Phase 4: OCR & Regex Parser
1. Pass the processed, thresholded frame to `pytesseract.image_to_string()`.
2. Write a regex-based extraction class to parse the raw text. It must extract:
    *   `Mission Title` (usually the top distinct line).
    *   `Reward` (extract the numeric aUEC value, stripping the currency symbols).
    *   `Objectives` (Look for the "PRIMARY OBJECTIVES" header and extract bullet points).
3. Return this data as a clean Python dictionary.

### Phase 5: Network Output & Reliability
1. Implement a module using `requests` to POST the JSON dictionary to the configured `api_endpoint`.
2. Wrap the network call in a background thread or asynchronous task so it doesn't block the screen capture loop.
3. Add robust `logging` to a local `service.log` file to track OCR failures, network timeouts, and general state changes without bothering the user.
