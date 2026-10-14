package com.lucaguelfi.csf.vision;

/**
 * Opzioni del motore di visione (equivalente di CSFVisionOptions del bridge iOS).
 * Un percorso modello nullo disattiva il relativo task: si carica solo il modello della modalità in uso.
 * I percorsi sono relativi agli asset dell'APK (es. "models/pose_landmarker_full.task").
 */
public final class CsfVisionOptions {
    private String poseModelPath;
    private String handModelPath;
    private String faceModelPath;
    private int maxPoses = 1;
    private int maxHands = 2;
    private int maxFaces = 1;
    private boolean useGpu = true;
    private float minConfidence = 0.5f;

    public String getPoseModelPath() { return poseModelPath; }
    public void setPoseModelPath(String value) { poseModelPath = value; }

    public String getHandModelPath() { return handModelPath; }
    public void setHandModelPath(String value) { handModelPath = value; }

    public String getFaceModelPath() { return faceModelPath; }
    public void setFaceModelPath(String value) { faceModelPath = value; }

    public int getMaxPoses() { return maxPoses; }
    public void setMaxPoses(int value) { maxPoses = value; }

    public int getMaxHands() { return maxHands; }
    public void setMaxHands(int value) { maxHands = value; }

    public int getMaxFaces() { return maxFaces; }
    public void setMaxFaces(int value) { maxFaces = value; }

    /** GPU (OpenGL) per l'inferenza; se il dispositivo non la supporta il motore ripiega sulla CPU. */
    public boolean getUseGpu() { return useGpu; }
    public void setUseGpu(boolean value) { useGpu = value; }

    public float getMinConfidence() { return minConfidence; }
    public void setMinConfidence(float value) { minConfidence = value; }
}
