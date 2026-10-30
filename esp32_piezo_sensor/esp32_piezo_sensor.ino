/********************************************************
  HY RESEARCH / DARVIS CORE
  ESP32 + Piezo Sensor + WiFiManager + PC Receiver (UDP)
  AO = GPIO34, DO = GPIO25
  Samples the sensor at 1 kHz and broadcasts the peak of
  every 50 ms window on the local network (UDP port 4210),
  shown on the PC by receiver/PiezoReceiver.exe
********************************************************/

// Blynk is disabled. To also send to the Blynk app (V0/V1), set this to 1,
// fill in your own values below and install the Blynk library.
#define USE_BLYNK 0

#if USE_BLYNK
#define BLYNK_TEMPLATE_ID   "YOUR_TEMPLATE_ID"
#define BLYNK_TEMPLATE_NAME "YOUR_TEMPLATE_NAME"
#define BLYNK_AUTH_TOKEN    "YOUR_AUTH_TOKEN"
#endif

#include <WiFi.h>
#include <WiFiManager.h>
#if USE_BLYNK
#include <BlynkSimpleEsp32.h>
#endif

#define SENSOR_AO 34
#define SENSOR_DO 25

// DO level that means "threshold passed". Set to LOW if your module's DO is active-low
// (the receiver shows TRIGGERED all the time while the sensor is still).
#define DO_ACTIVE_LEVEL HIGH

// Must match Config.Port in receiver/PiezoReceiver.cs
const uint16_t RECEIVER_PORT = 4210;
WiFiUDP udp;

// Sample fast and send the peak of each window, so short cough/swallow spikes are not missed
const unsigned long SAMPLE_INTERVAL_US = 1000;  // 1 kHz
const unsigned long SEND_INTERVAL = 50;         // 20 packets per second
unsigned long lastSample = 0;
unsigned long lastSend = 0;

// Current window
int winMax = 0;
int winMin = 4095;
unsigned long winSum = 0;
unsigned int winCount = 0;
bool winTriggered = false;
unsigned long seq = 0;

unsigned long lastWiFiCheck = 0;
const unsigned long WIFI_CHECK_INTERVAL = 10000;

void sampleSensor() {
  int analogValue = analogRead(SENSOR_AO);
  if (analogValue > winMax) winMax = analogValue;
  if (analogValue < winMin) winMin = analogValue;
  winSum += analogValue;
  winCount++;

  if (digitalRead(SENSOR_DO) == DO_ACTIVE_LEVEL) {
    winTriggered = true;
  }
}

void sendSensorData() {
  if (winCount == 0) {
    return;
  }

  int avg = winSum / winCount;

  Serial.print("AO peak = ");
  Serial.print(winMax);
  Serial.print(" | min = ");
  Serial.print(winMin);
  Serial.print(" | avg = ");
  Serial.print(avg);
  Serial.print(" | DO = ");
  Serial.print(winTriggered ? "TRIGGERED" : "idle");

  if (WiFi.status() == WL_CONNECTED) {
    Serial.print(" | WiFi = CONNECTED");
    Serial.print(" | IP = ");
    Serial.print(WiFi.localIP());
  } else {
    Serial.print(" | WiFi = DISCONNECTED");
  }

  Serial.println();

  // Broadcast to the PC receiver, e.g.
  // {"seq":42,"ao":3100,"lo":180,"avg":900,"do":1,"n":50,"rssi":-58,"up":123456}
  if (WiFi.status() == WL_CONNECTED) {
    char packet[128];
    snprintf(packet, sizeof(packet),
             "{\"seq\":%lu,\"ao\":%d,\"lo\":%d,\"avg\":%d,\"do\":%d,\"n\":%u,\"rssi\":%d,\"up\":%lu}",
             seq, winMax, winMin, avg, winTriggered ? 1 : 0, winCount, (int)WiFi.RSSI(), millis());
    udp.beginPacket(IPAddress(255, 255, 255, 255), RECEIVER_PORT);
    udp.print(packet);
    udp.endPacket();
  }

#if USE_BLYNK
  // Every 4th window (5 per second, as before) to stay within Blynk limits
  if (Blynk.connected() && seq % 4 == 0) {
    Blynk.virtualWrite(V0, winMax);
    Blynk.virtualWrite(V1, winTriggered ? 1 : 0);
  }
#endif

  seq++;
  winMax = 0;
  winMin = 4095;
  winSum = 0;
  winCount = 0;
  winTriggered = false;
}

void setup() {
  Serial.begin(115200);
  delay(500);

  pinMode(SENSOR_DO, INPUT);
  analogReadResolution(12);

  Serial.println();
  Serial.println("HY RESEARCH / DARVIS CORE");
  Serial.println("ESP32 PIEZO + PC RECEIVER (UDP)");

  WiFi.mode(WIFI_STA);
  WiFi.setAutoReconnect(true);

  WiFiManager wm;
  wm.setConfigPortalTimeout(180);

  if (!wm.autoConnect("DARVIS-PIEZO")) {
    Serial.println("WiFi setup failed. Restarting...");
    delay(1000);
    ESP.restart();
  }

  Serial.print("WiFi Connected. IP: ");
  Serial.println(WiFi.localIP());

  // Keep the radio awake so the 20 packets/s stream goes out on time
  WiFi.setSleep(false);

#if USE_BLYNK
  // เชื่อมต่อ Blynk โดยไม่บล็อกการอ่านเซนเซอร์นาน
  Blynk.config(BLYNK_AUTH_TOKEN);
  Blynk.connect(5000);
#endif

  Serial.print("Sampling at 1 kHz, sending the peak every 50 ms to UDP port ");
  Serial.println(RECEIVER_PORT);
  Serial.println("System Ready");
}

void loop() {
  if (micros() - lastSample >= SAMPLE_INTERVAL_US) {
    lastSample = micros();
    sampleSensor();
  }

  if (millis() - lastSend >= SEND_INTERVAL) {
    lastSend = millis();
    sendSensorData();
  }

  if (WiFi.status() != WL_CONNECTED &&
      millis() - lastWiFiCheck >= WIFI_CHECK_INTERVAL) {
    lastWiFiCheck = millis();
    Serial.println("WiFi lost. Reconnecting...");
    WiFi.reconnect();
  }

#if USE_BLYNK
  if (WiFi.status() == WL_CONNECTED) {
    if (!Blynk.connected()) {
      Blynk.connect(100);
    }
    Blynk.run();
  }
#endif
}
