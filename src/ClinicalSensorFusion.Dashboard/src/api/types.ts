// Tipi del contratto JSON del server (ASP.NET Core, camelCase, enum come stringhe).
// Mantenere allineati con src/ClinicalSensorFusion.Server/Endpoints/*.cs.

export type Sex = 'Unknown' | 'Female' | 'Male' | 'Other';
export type CaptureMode = 'BodyLidar' | 'HandsLidar' | 'FaceTrueDepth';
export type Joint =
  | 'LeftShoulderElevation' | 'RightShoulderElevation'
  | 'LeftElbowFlexion' | 'RightElbowFlexion'
  | 'LeftHipFlexion' | 'RightHipFlexion'
  | 'LeftKneeFlexion' | 'RightKneeFlexion'
  | 'LeftAnkle' | 'RightAnkle';

export interface CurrentUser { userName: string; displayName: string }

export interface PagedResult<T> { items: T[]; total: number; page: number; pageSize: number }

export interface PatientListItem {
  id: string; code: string; firstName: string | null; lastName: string | null; birthDate: string | null;
  sex: Sex; isPlaceholder: boolean; sessionCount: number; lastSessionAt: string | null;
}

export interface PatientDetail extends Omit<PatientListItem, 'lastSessionAt'> {
  fiscalCode: string | null; phone: string | null; email: string | null; address: string | null;
  diagnosis: string | null; notes: string | null; createdAt: string; updatedAt: string; version: number;
}

export interface PatientInput {
  code: string; firstName: string | null; lastName: string | null; birthDate: string | null; sex: Sex;
  fiscalCode: string | null; phone: string | null; email: string | null; address: string | null;
  diagnosis: string | null; notes: string | null; version: number | null;
}

export interface SessionListItem {
  id: string; patientId: string; patientCode: string; patientName: string | null; mode: CaptureMode;
  startedAt: string; durationSeconds: number | null; operatorName: string; tremorFrequencyHz: number | null;
  lidarCoverage: number | null; audioCount: number; analysisCount: number;
}

export interface SummaryRow { section: string; label: string; value: string; warning: boolean }

export interface AudioItem { id: string; createdAt: string; durationSeconds: number; features: unknown; available: boolean; sizeBytes: number | null }

export interface AnalysisItem { id: string; createdAt: string; model: string; language: string; operatorId: string; response: string }

export interface SessionDetail {
  id: string; patientId: string; patientCode: string; patientName: string | null; mode: CaptureMode;
  startedAt: string; endedAt: string | null; operatorId: string; operatorName: string; notes: string | null;
  deviceModel: string | null; summary: unknown; report: SummaryRow[]; streams: Record<string, number>;
  audio: AudioItem[]; analyses: AnalysisItem[];
}

export interface JointRange { min: number; max: number; mean: number; samples: number }

export interface TrendPoint {
  sessionId: string; startedAt: string; mode: CaptureMode; durationSeconds: number | null;
  rom: Partial<Record<Joint, JointRange>>; tremorFrequencyHz: number | null; tremorRmsMs2: number | null;
  cadenceStepsPerMinute: number | null; steps: number | null; audioRmsDbfs: number | null;
}

export interface WeeklyActivity { weekStart: string; mode: CaptureMode; sessions: number }
export interface TremorPoint { sessionId: string; patientCode: string; startedAt: string; frequencyHz: number; rmsMs2: number; durationSeconds: number }
export interface JointAverage { joint: Joint; name: string; meanRom: number; sessions: number }

export interface Overview {
  patients: number; placeholderPatients: number; sessions: number; sessionsLast30Days: number;
  recordedHours: number; audioFiles: number; weekly: WeeklyActivity[]; jointAverages: JointAverage[];
  tremor: TremorPoint[]; recent: SessionListItem[];
}

export interface PoseFrame { t: number; points: (number | null)[]; angles: (number | null)[] }
export interface PoseReplay { frameCount: number; duration: number; jointNames: Joint[]; connections: [number, number][]; frames: PoseFrame[] }

export interface Spectrogram { times: number[]; frequencies: number[]; powerDb: number[][]; sampleRateHz: number }

export const modeLabel: Record<CaptureMode, string> = {
  BodyLidar: 'Corpo · LiDAR',
  HandsLidar: 'Mani · LiDAR',
  FaceTrueDepth: 'Volto · TrueDepth',
};

export const jointLabel: Record<Joint, string> = {
  LeftShoulderElevation: 'Spalla sx', RightShoulderElevation: 'Spalla dx',
  LeftElbowFlexion: 'Gomito sx', RightElbowFlexion: 'Gomito dx',
  LeftHipFlexion: 'Anca sx', RightHipFlexion: 'Anca dx',
  LeftKneeFlexion: 'Ginocchio sx', RightKneeFlexion: 'Ginocchio dx',
  LeftAnkle: 'Caviglia sx', RightAnkle: 'Caviglia dx',
};

export const allJoints = Object.keys(jointLabel) as Joint[];
