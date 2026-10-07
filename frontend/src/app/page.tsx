'use client';

import React, { useState, useEffect, useRef } from 'react';
import { 
  Bot, 
  Send, 
  Mic, 
  MicOff, 
  Calendar, 
  Clock, 
  Settings, 
  CheckCircle2, 
  XCircle, 
  AlertCircle, 
  Activity, 
  PhoneCall, 
  User, 
  Sparkles,
  RefreshCw,
  FileText,
  ShieldCheck,
  ChevronRight,
  Volume2,
  Square,
  Search,
  UploadCloud,
  BookOpen
} from 'lucide-react';

interface Agent {
  id: string;
  name: string;
  systemPrompt: string;
  modelName: string;
  languageCode: string;
  temperature: number;
  isActive: boolean;
  allowedTools: string[];
}

interface Message {
  role: 'user' | 'assistant' | 'tool';
  content: string;
  toolName?: string;
  toolCallId?: string;
  time?: string;
}

interface Booking {
  id: string;
  customerName: string;
  customerPhone: string;
  serviceName: string;
  cairoTimeFormatted: string;
  status: string;
  idempotencyKey: string;
}

interface PendingBookingCard {
  id: string;
  conversationId: string;
  customerName: string;
  customerPhone: string;
  serviceName: string;
  cairoTimeFormatted: string;
  requestHash: string;
  status: 'Pending' | 'Confirmed' | 'Invalidated' | 'FailedCapacity';
  confirmedBookingId?: string;
  errorMessage?: string;
}

const createUUID = () => {
  if (typeof crypto !== 'undefined' && crypto.randomUUID) {
    return crypto.randomUUID();
  }
  return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, c => {
    const r = (Math.random() * 16) | 0;
    return (c === 'x' ? r : (r & 0x3) | 0x8).toString(16);
  });
};

interface Slot {
  id: string;
  serviceName: string;
  cairoTimeFormatted: string;
  totalCapacity: number;
  bookedCapacity: number;
  isAvailable: boolean;
}

interface HealthStatus {
  status: string;
  cairoTime: string;
  isCairoBusinessHours: boolean;
  businessSchedule: string;
  databaseConnected: boolean;
  configuredLlmProvider: string;
  defaultModel: string;
}

interface KnowledgeDoc {
  id: string;
  title: string;
  fileName: string;
  category: string;
  chunkCount: number;
  createdAtUtc: string;
}

export default function Dashboard() {
  const [activeTab, setActiveTab] = useState<'chat' | 'voice' | 'knowledge' | 'settings' | 'bookings' | 'logs'>('chat');
  const [health, setHealth] = useState<HealthStatus | null>(null);
  const [agents, setAgents] = useState<Agent[]>([]);
  const [selectedAgent, setSelectedAgent] = useState<Agent | null>(null);
  
  // Chat state
  const [messages, setMessages] = useState<Message[]>([
    {
      role: 'assistant',
      content: 'أهلاً بحضرتك في عيادة النور التخصصية! معاكِ سارة، مساعدة العيادة. أقدر أساعدك في معرفة المواعيد المتاحة أو حجز كشف باطنة. تحب تستفسر عن إيه النهاردة؟',
      time: 'الآن'
    }
  ]);
  const [inputMessage, setInputMessage] = useState('');
  const [isStreaming, setIsStreaming] = useState(false);
  const [conversationId, setConversationId] = useState<string>('');
  const [customerToken, setCustomerToken] = useState<string>('');
  const [adminKey, setAdminKey] = useState<string>('');
  const [adminKeyInput, setAdminKeyInput] = useState<string>('');
  const [showAdminModal, setShowAdminModal] = useState<boolean>(false);
  const [isRecording, setIsRecording] = useState(false);
  const [toolLogs, setToolLogs] = useState<any[]>([]);
  const [pendingCard, setPendingCard] = useState<PendingBookingCard | null>(null);
  const [isConfirming, setIsConfirming] = useState(false);

  // Bookings state
  const [bookings, setBookings] = useState<Booking[]>([]);
  const [slots, setSlots] = useState<Slot[]>([]);
  const [loadingData, setLoadingData] = useState(false);

  // Knowledge & RAG state
  const [knowledgeDocs, setKnowledgeDocs] = useState<KnowledgeDoc[]>([]);
  const [newDocTitle, setNewDocTitle] = useState('');
  const [newDocCategory, setNewDocCategory] = useState('Services');
  const [newDocContent, setNewDocContent] = useState('');
  const [isIngesting, setIsIngesting] = useState(false);
  const [ragQuery, setRagQuery] = useState('');
  const [ragResults, setRagResults] = useState<any[]>([]);
  const [isSearchingRag, setIsSearchingRag] = useState(false);

  // Voice tab state
  const [voiceSessionId, setVoiceSessionId] = useState<string>('');
  const [voiceText, setVoiceText] = useState('');
  const [voiceResponseText, setVoiceResponseText] = useState<string>('');
  const [isVoiceProcessing, setIsVoiceProcessing] = useState(false);
  const [isVoiceSpeaking, setIsVoiceSpeaking] = useState(false);
  const [voiceInterrupted, setVoiceInterrupted] = useState(false);
  const [voiceTurnId, setVoiceTurnId] = useState<number>(0);
  const audioPlayerRef = useRef<HTMLAudioElement | null>(null);
  const isInterruptedRef = useRef<boolean>(false);
  const activeVoiceRequestIdRef = useRef<number>(0);

  // Settings form state
  const [agentForm, setAgentForm] = useState<Partial<Agent>>({});
  const [saveSuccess, setSaveSuccess] = useState(false);

  const messagesEndRef = useRef<HTMLDivElement>(null);

  const scrollToBottom = () => {
    messagesEndRef.current?.scrollIntoView({ behavior: 'smooth' });
  };

  useEffect(() => {
    scrollToBottom();
  }, [messages]);

  // Load persisted session and admin keys from browser sessionStorage on mount
  useEffect(() => {
    if (typeof window !== 'undefined') {
      const savedAdmin = sessionStorage.getItem('masryvoice_admin_key');
      if (savedAdmin) {
        setAdminKey(savedAdmin);
        setAdminKeyInput(savedAdmin);
      }
      const savedConv = sessionStorage.getItem('masryvoice_conversation_id');
      const savedToken = sessionStorage.getItem('masryvoice_customer_token');
      if (savedConv) setConversationId(savedConv);
      if (savedToken) setCustomerToken(savedToken);
    }
  }, []);

  const updateSessionCredentials = (newConvId?: string, newToken?: string) => {
    if (newConvId) {
      setConversationId(newConvId);
      if (typeof window !== 'undefined') sessionStorage.setItem('masryvoice_conversation_id', newConvId);
    }
    if (newToken) {
      setCustomerToken(newToken);
      if (typeof window !== 'undefined') sessionStorage.setItem('masryvoice_customer_token', newToken);
    }
  };

  const handleAdminLogin = (keyToSave: string) => {
    const trimmed = keyToSave.trim();
    setAdminKey(trimmed);
    if (typeof window !== 'undefined') {
      if (trimmed) sessionStorage.setItem('masryvoice_admin_key', trimmed);
      else sessionStorage.removeItem('masryvoice_admin_key');
    }
    setShowAdminModal(false);
    fetchState(trimmed);
  };

  // Fetch initial system state
  const fetchState = async (overrideAdminKey?: string) => {
    const keyToUse = overrideAdminKey !== undefined ? overrideAdminKey : adminKey;
    try {
      setLoadingData(true);
      const adminHeaders: Record<string, string> = {};
      if (keyToUse) {
        adminHeaders['X-Admin-Key'] = keyToUse;
      }

      const [healthRes, agentsRes, bookingsRes, slotsRes, docsRes] = await Promise.all([
        fetch('/api/health').then(r => r.json()).catch(() => null),
        fetch('/api/agents').then(r => r.json()).catch(() => []),
        fetch('/api/bookings', { headers: adminHeaders }).then(r => r.json()).catch(() => []),
        fetch('/api/slots').then(r => r.json()).catch(() => []),
        fetch('/api/knowledge/documents', { headers: adminHeaders }).then(r => r.json()).catch(() => [])
      ]);

      if (healthRes) setHealth(healthRes);
      if (agentsRes && agentsRes.length > 0) {
        setAgents(agentsRes);
        setSelectedAgent(agentsRes[0]);
        setAgentForm(agentsRes[0]);
      }
      if (bookingsRes && Array.isArray(bookingsRes)) setBookings(bookingsRes);
      if (slotsRes) setSlots(slotsRes);
      if (docsRes && Array.isArray(docsRes)) setKnowledgeDocs(docsRes);
    } catch (err) {
      console.error('Error fetching state:', err);
    } finally {
      setLoadingData(false);
    }
  };

  useEffect(() => {
    fetchState();
    const interval = setInterval(() => fetchState(), 15000);
    return () => clearInterval(interval);
  }, [adminKey]);

  // Send message with SSE streaming
  const handleSendMessage = async (textToSend?: string) => {
    const text = (textToSend || inputMessage).trim();
    if (!text || isStreaming || !selectedAgent) return;

    setInputMessage('');
    const userMsg: Message = { role: 'user', content: text, time: new Date().toLocaleTimeString('ar-EG') };
    setMessages(prev => [...prev, userMsg]);
    setIsStreaming(true);

    // Placeholder for incoming assistant message
    const assistantMsgIndex = messages.length + 1;
    setMessages(prev => [...prev, { role: 'assistant', content: '', time: 'جاري الكتابة...' }]);

    try {
      const headers: Record<string, string> = { 'Content-Type': 'application/json' };
      if (customerToken) {
        headers['X-Customer-Token'] = customerToken;
      }

      const response = await fetch('/api/chat/stream', {
        method: 'POST',
        headers,
        body: JSON.stringify({
          agentId: selectedAgent.id,
          conversationId: conversationId || null,
          message: text
        })
      });

      if (!response.ok) throw new Error('فشل الاتصال بالخادم');
      if (!response.body) throw new Error('لا توجد استجابة');

      const reader = response.body.getReader();
      const decoder = new TextDecoder();
      let assistantText = '';

      while (true) {
        const { value, done } = await reader.read();
        if (done) break;

        const chunk = decoder.decode(value, { stream: true });
        const lines = chunk.split('\n');

        for (const line of lines) {
          if (line.startsWith('data: ')) {
            const dataStr = line.substring(6).trim();
            if (!dataStr) continue;

            try {
              const data = JSON.parse(dataStr);
              if (data.type === 'session') {
                updateSessionCredentials(data.conversationId, data.customerToken);
              } else if (data.type === 'token') {
                assistantText += data.content;
                setMessages(prev => {
                  const updated = [...prev];
                  const last = updated[updated.length - 1];
                  if (last && last.role === 'assistant') {
                    last.content = assistantText;
                    last.time = new Date().toLocaleTimeString('ar-EG');
                  }
                  return updated;
                });
              } else if (data.type === 'tool_call') {
                setToolLogs(prev => [
                  { type: 'call', tool: data.content, meta: data.metadata, time: new Date().toLocaleTimeString('ar-EG') },
                  ...prev
                ]);
              } else if (data.type === 'tool_result') {
                setToolLogs(prev => [
                  { type: 'result', tool: data.content, meta: data.metadata, time: new Date().toLocaleTimeString('ar-EG') },
                  ...prev
                ]);
                
                // If StageBooking created a pending booking, display the explicit confirmation card
                if (data.content === 'StageBooking' && data.metadata?.Success && data.metadata?.Data?.pendingBookingId) {
                  const d = data.metadata.Data;
                  if (d.customerToken) updateSessionCredentials(d.conversationId, d.customerToken);
                  setPendingCard({
                    id: d.pendingBookingId,
                    conversationId: d.conversationId || conversationId,
                    customerName: d.customerName,
                    customerPhone: d.customerPhone,
                    serviceName: d.service,
                    cairoTimeFormatted: d.cairoTime,
                    requestHash: d.requestHash,
                    status: 'Pending'
                  });
                }

                // Refresh bookings and slots after tool execution
                fetchState();
              }
            } catch (e) {
              console.error('Error parsing SSE data:', e);
            }
          }
        }
      }
    } catch (err: any) {
      setMessages(prev => [
        ...prev,
        { role: 'assistant', content: `عذراً، حدث خطأ: ${err.message}`, time: 'خطأ' }
      ]);
    } finally {
      setIsStreaming(false);
    }
  };

  // Explicit Customer Confirmation Action (Invokes Server-side Application Service)
  const handleConfirmBooking = async () => {
    if (!pendingCard || isConfirming || pendingCard.status !== 'Pending') return;
    setIsConfirming(true);
    try {
      const headers: Record<string, string> = { 'Content-Type': 'application/json' };
      if (customerToken) {
        headers['X-Customer-Token'] = customerToken;
      }

      const res = await fetch('/api/bookings/confirm', {
        method: 'POST',
        headers,
        body: JSON.stringify({
          conversationId: pendingCard.conversationId || conversationId,
          pendingBookingId: pendingCard.id,
          expectedRequestHash: pendingCard.requestHash
        })
      });
      const data = await res.json();
      const isSuccess = data.success !== undefined ? Boolean(data.success) : Boolean(data.Success);
      const resData = data.data !== undefined ? data.data : data.Data;
      const resMsg = data.message !== undefined ? data.message : data.Message;
      const resErrorCode = data.errorCode !== undefined ? data.errorCode : data.ErrorCode;

      if (res.ok && isSuccess) {
        if (data.customerToken) updateSessionCredentials(conversationId, data.customerToken);
        setPendingCard(prev => prev ? {
          ...prev,
          status: 'Confirmed',
          confirmedBookingId: resData?.bookingId
        } : null);
        setMessages(prev => [
          ...prev,
          {
            role: 'assistant',
            content: `✅ تم تأكيد وتثبيت الحجز بنجاح في قاعدة البيانات!\nرقم الحجز: ${resData?.bookingId}\nالاسم: ${resData?.customerName}\nالموعد: ${resData?.cairoTime}`,
            time: new Date().toLocaleTimeString('ar-EG')
          }
        ]);
        fetchState();
      } else {
        const err = resMsg || 'تعذر تأكيد الحجز';
        setPendingCard(prev => prev ? {
          ...prev,
          status: resErrorCode === 'STALE_PENDING_BOOKING' ? 'Invalidated' : prev.status,
          errorMessage: err
        } : null);
      }
    } catch (err: any) {
      setPendingCard(prev => prev ? { ...prev, errorMessage: err.message } : null);
    } finally {
      setIsConfirming(false);
    }
  };

  // Push to talk microphone toggle (Web Audio simulator)
  const toggleRecording = () => {
    if (!isRecording) {
      setIsRecording(true);
      // Simulate listening and transcribe after 2 seconds
      setTimeout(() => {
        setIsRecording(false);
        handleSendMessage('عايز أعرف إيه المواعيد المتاحة بكرة لكشف الباطنة؟');
      }, 2500);
    } else {
      setIsRecording(false);
    }
  };

  // Knowledge ingestion handler
  const handleIngestDoc = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!newDocTitle.trim() || !newDocContent.trim()) return;
    if (!adminKey) {
      setShowAdminModal(true);
      return;
    }
    setIsIngesting(true);
    try {
      const res = await fetch('/api/knowledge/ingest', {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          'X-Admin-Key': adminKey
        },
        body: JSON.stringify({
          title: newDocTitle,
          content: newDocContent,
          category: newDocCategory,
          fileName: `${newDocTitle.replace(/\s+/g, '_')}.md`
        })
      });
      if (res.ok) {
        setNewDocTitle('');
        setNewDocContent('');
        fetchState();
      } else if (res.status === 401) {
        alert('مفتاح الإدارة غير صحيح أو غير مصرح به');
        setShowAdminModal(true);
      }
    } catch (err) {
      console.error(err);
    } finally {
      setIsIngesting(false);
    }
  };

  // RAG Search tester handler
  const handleSearchRag = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!ragQuery.trim()) return;
    setIsSearchingRag(true);
    try {
      const res = await fetch(`/api/knowledge/search?query=${encodeURIComponent(ragQuery)}`);
      if (res.ok) {
        const data = await res.json();
        setRagResults(data);
      }
    } catch (err) {
      console.error(err);
    } finally {
      setIsSearchingRag(false);
    }
  };

  // Voice Session & Barge-in handlers
  const handleStartVoiceSession = async () => {
    isInterruptedRef.current = false;
    try {
      const tokenToUse = customerToken || (typeof window !== 'undefined' ? sessionStorage.getItem('masryvoice_customer_token') : null);
      const convToUse = conversationId || (typeof window !== 'undefined' ? sessionStorage.getItem('masryvoice_conversation_id') : null);
      const headers: Record<string, string> = { 'Content-Type': 'application/json' };
      if (tokenToUse) headers['X-Customer-Token'] = tokenToUse;
      const res = await fetch('/api/voice/session', {
        method: 'POST',
        headers,
        body: JSON.stringify({ conversationId: convToUse || null })
      });
      if (res.ok) {
        const data = await res.json();
        setVoiceSessionId(data.sessionId);
        updateSessionCredentials(data.conversationId, data.customerToken);
        setVoiceInterrupted(false);
      } else {
        console.error('Failed to create voice session:', res.status, await res.text());
      }
    } catch (err) {
      console.error(err);
    }
  };

  const handleBargeInInterrupt = async () => {
    if (!voiceSessionId) return;
    isInterruptedRef.current = true;
    activeVoiceRequestIdRef.current++; // Invalidate any pending in-flight request
    setIsVoiceProcessing(false);
    try {
      if (audioPlayerRef.current) {
        audioPlayerRef.current.pause();
        audioPlayerRef.current.currentTime = 0;
        audioPlayerRef.current.src = '';
      }
      setIsVoiceSpeaking(false);
      setVoiceInterrupted(true);
      const tokenToUse = customerToken || (typeof window !== 'undefined' ? sessionStorage.getItem('masryvoice_customer_token') : null);
      const headers: Record<string, string> = { 'Content-Type': 'application/json' };
      if (tokenToUse) headers['X-Customer-Token'] = tokenToUse;
      await fetch('/api/voice/interrupt', {
        method: 'POST',
        headers,
        body: JSON.stringify({ sessionId: voiceSessionId })
      });
    } catch (err) {
      console.error(err);
    }
  };

  const handleSendVoiceTurn = async (messageText?: string) => {
    const textToSend = messageText || voiceText;
    if (!textToSend.trim() || !selectedAgent) return;
    isInterruptedRef.current = false;
    const currentRequestId = ++activeVoiceRequestIdRef.current;
    let sId = voiceSessionId;
    let currentConv = conversationId || (typeof window !== 'undefined' ? sessionStorage.getItem('masryvoice_conversation_id') : null);
    let currentToken = customerToken || (typeof window !== 'undefined' ? sessionStorage.getItem('masryvoice_customer_token') : null);
    if (!sId) {
      const headers: Record<string, string> = { 'Content-Type': 'application/json' };
      if (currentToken) headers['X-Customer-Token'] = currentToken;
      const res = await fetch('/api/voice/session', {
        method: 'POST',
        headers,
        body: JSON.stringify({ conversationId: currentConv || null })
      });
      const data = await res.json();
      sId = data.sessionId;
      currentConv = data.conversationId;
      currentToken = data.customerToken;
      setVoiceSessionId(sId);
      updateSessionCredentials(data.conversationId, data.customerToken);
    }

    setIsVoiceProcessing(true);
    setVoiceInterrupted(false);
    try {
      const headers: Record<string, string> = { 'Content-Type': 'application/json' };
      if (currentToken) headers['X-Customer-Token'] = currentToken;
      const res = await fetch('/api/voice/turn', {
        method: 'POST',
        headers,
        body: JSON.stringify({
          sessionId: sId,
          conversationId: currentConv,
          agentId: selectedAgent.id,
          message: textToSend
        })
      });

      if (res.ok) {
        const data = await res.json();

        // Reject stale/interrupted responses before applying turn ID, input, or response-state updates!
        if (data.interrupted || isInterruptedRef.current || currentRequestId !== activeVoiceRequestIdRef.current) {
          setIsVoiceSpeaking(false);
          setVoiceInterrupted(true);
          return;
        }

        setVoiceTurnId(data.turnId);
        setVoiceResponseText(data.text || '');
        setVoiceText('');

        if (data.audioBase64) {
          const audioSrc = `data:audio/wav;base64,${data.audioBase64}`;
          if (audioPlayerRef.current && !isInterruptedRef.current && currentRequestId === activeVoiceRequestIdRef.current) {
            audioPlayerRef.current.src = audioSrc;
            setIsVoiceSpeaking(true);
            audioPlayerRef.current.play().catch(() => {});
            audioPlayerRef.current.onended = () => setIsVoiceSpeaking(false);
          }
        }
      } else if (res.status === 499) {
        setIsVoiceSpeaking(false);
        setVoiceInterrupted(true);
      }
    } catch (err) {
      console.error(err);
    } finally {
      setIsVoiceProcessing(false);
    }
  };

  // Save Agent Settings
  const handleSaveSettings = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!selectedAgent) return;

    if (!adminKey) {
      setShowAdminModal(true);
      return;
    }

    try {
      const res = await fetch('/api/agents', {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          'X-Admin-Key': adminKey
        },
        body: JSON.stringify({
          ...selectedAgent,
          ...agentForm
        })
      });

      if (res.ok) {
        setSaveSuccess(true);
        setTimeout(() => setSaveSuccess(false), 3000);
        fetchState();
      } else if (res.status === 401) {
        alert('مفتاح الإدارة غير صحيح أو غير مصرح به');
        setShowAdminModal(true);
      }
    } catch (err) {
      alert('حدث خطأ أثناء حفظ الإعدادات');
    }
  };

  return (
    <div style={{ minHeight: '100vh', display: 'flex', flexDirection: 'column' }}>
      {/* Top Navbar */}
      <header style={{
        background: 'rgba(17, 24, 39, 0.85)',
        borderBottom: '1px solid var(--border-color)',
        padding: '16px 24px',
        display: 'flex',
        alignItems: 'center',
        justifyContent: 'space-between',
        position: 'sticky',
        top: 0,
        zIndex: 50,
        backdropFilter: 'blur(10px)'
      }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: '12px' }}>
          <div style={{
            background: 'linear-gradient(135deg, #10b981 0%, #06b6d4 100%)',
            padding: '10px',
            borderRadius: '12px',
            display: 'flex',
            alignItems: 'center',
            justifyContent: 'center',
            boxShadow: '0 0 15px rgba(16, 185, 129, 0.3)'
          }}>
            <Bot size={24} color="#fff" />
          </div>
          <div>
            <h1 style={{ fontSize: '1.25rem', fontWeight: 700, color: '#f9fafb' }}>
              MasryVoice <span style={{ fontSize: '0.85rem', color: '#10b981', fontWeight: 500 }}>| منصة الوكيل الصوتي المصري</span>
            </h1>
            <p style={{ fontSize: '0.75rem', color: 'var(--text-muted)' }}>
              عيادة النور التخصصية • اللهجة المصرية العامية
            </p>
          </div>
        </div>

        {/* System Health / Cairo Time Indicator */}
        <div style={{ display: 'flex', alignItems: 'center', gap: '16px' }}>
          <div style={{
            display: 'flex',
            alignItems: 'center',
            gap: '8px',
            background: 'rgba(255, 255, 255, 0.05)',
            padding: '6px 14px',
            borderRadius: '20px',
            fontSize: '0.8rem',
            border: '1px solid var(--border-color)'
          }}>
            <Clock size={14} color="#10b981" />
            <span>توقيت القاهرة: <strong style={{ color: '#fff' }}>{health?.cairoTime?.substring(11, 16) || '09:30'}</strong></span>
            <span style={{
              display: 'inline-block',
              width: '8px',
              height: '8px',
              borderRadius: '50%',
              backgroundColor: health?.isCairoBusinessHours ? '#10b981' : '#f59e0b'
            }} />
            <span style={{ color: health?.isCairoBusinessHours ? '#10b981' : '#f59e0b', fontSize: '0.75rem' }}>
              {health?.isCairoBusinessHours ? 'ساعات العمل الرسمية' : 'خارج أوقات العمل'}
            </span>
          </div>

          <div style={{
            display: 'flex',
            alignItems: 'center',
            gap: '6px',
            background: 'rgba(6, 182, 212, 0.1)',
            padding: '6px 12px',
            borderRadius: '20px',
            fontSize: '0.8rem',
            color: '#06b6d4',
            border: '1px solid rgba(6, 182, 212, 0.2)'
          }}>
            <Activity size={14} />
            <span>النموذج: <strong>{health?.defaultModel || 'qwen2.5:1.5b'}</strong> ({health?.configuredLlmProvider || 'Ollama'})</span>
          </div>

          {/* Admin Auth Status / Login Action */}
          <button
            onClick={() => {
              setAdminKeyInput(adminKey);
              setShowAdminModal(true);
            }}
            id="admin-auth-btn"
            style={{
              display: 'flex',
              alignItems: 'center',
              gap: '6px',
              background: adminKey ? 'rgba(16, 185, 129, 0.15)' : 'rgba(239, 68, 68, 0.15)',
              border: adminKey ? '1px solid rgba(16, 185, 129, 0.3)' : '1px solid rgba(239, 68, 68, 0.3)',
              color: adminKey ? '#10b981' : '#f87171',
              padding: '6px 14px',
              borderRadius: '20px',
              fontSize: '0.8rem',
              cursor: 'pointer',
              fontWeight: 600
            }}
          >
            <ShieldCheck size={14} />
            <span>{adminKey ? 'الإدارة: مصرح' : 'تسجيل الإدارة'}</span>
          </button>
        </div>
      </header>

      {/* Admin Auth Modal */}
      {showAdminModal && (
        <div style={{
          position: 'fixed',
          top: 0,
          left: 0,
          right: 0,
          bottom: 0,
          backgroundColor: 'rgba(0,0,0,0.7)',
          zIndex: 100,
          display: 'flex',
          alignItems: 'center',
          justifyContent: 'center',
          backdropFilter: 'blur(4px)'
        }}>
          <div style={{
            background: '#1f2937',
            border: '1px solid var(--border-color)',
            borderRadius: '16px',
            padding: '24px',
            width: '90%',
            maxWidth: '440px',
            boxShadow: '0 20px 25px -5px rgba(0, 0, 0, 0.5)'
          }}>
            <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', marginBottom: '16px' }}>
              <div style={{ display: 'flex', alignItems: 'center', gap: '8px' }}>
                <ShieldCheck size={20} color="#10b981" />
                <h3 style={{ margin: 0, color: '#f9fafb', fontSize: '1.1rem', fontWeight: 600 }}>تسجيل مصادقة الإدارة</h3>
              </div>
              <button
                onClick={() => setShowAdminModal(false)}
                style={{ background: 'transparent', border: 'none', color: 'var(--text-muted)', cursor: 'pointer' }}
              >
                <XCircle size={18} />
              </button>
            </div>
            <p style={{ fontSize: '0.85rem', color: 'var(--text-muted)', marginBottom: '16px', lineHeight: 1.5 }}>
              يرجى إدخال مفتاح الإدارة السري للوصول إلى سجل الحجوزات وإدارة المعرفة وتعديل إعدادات الوكيل.
            </p>
            <input
              type="password"
              id="admin-key-modal-input"
              value={adminKeyInput}
              onChange={e => setAdminKeyInput(e.target.value)}
              placeholder="أدخل مفتاح الإدارة (X-Admin-Key)..."
              style={{
                width: '100%',
                padding: '10px 14px',
                borderRadius: '8px',
                border: '1px solid var(--border-color)',
                background: '#111827',
                color: '#fff',
                fontSize: '0.9rem',
                marginBottom: '16px',
                outline: 'none',
                boxSizing: 'border-box'
              }}
            />
            <div style={{ display: 'flex', gap: '10px', justifyContent: 'flex-end' }}>
              {adminKey && (
                <button
                  onClick={() => handleAdminLogin('')}
                  style={{
                    padding: '8px 16px',
                    borderRadius: '8px',
                    border: '1px solid rgba(239, 68, 68, 0.4)',
                    background: 'rgba(239, 68, 68, 0.15)',
                    color: '#f87171',
                    fontSize: '0.85rem',
                    cursor: 'pointer'
                  }}
                >
                  تسجيل خروج
                </button>
              )}
              <button
                onClick={() => handleAdminLogin(adminKeyInput)}
                id="save-admin-key-btn"
                style={{
                  padding: '8px 18px',
                  borderRadius: '8px',
                  border: 'none',
                  background: 'linear-gradient(135deg, #10b981 0%, #059669 100%)',
                  color: '#fff',
                  fontSize: '0.85rem',
                  fontWeight: 600,
                  cursor: 'pointer'
                }}
              >
                حفظ والمصادقة
              </button>
            </div>
          </div>
        </div>
      )}

      {/* Main Layout */}
      <div style={{ display: 'flex', flex: 1, padding: '24px', gap: '24px' }}>
        {/* Sidebar Navigation */}
        <aside style={{ width: '260px', display: 'flex', flexDirection: 'column', gap: '8px' }}>
          <button
            id="tab-chat-btn"
            onClick={() => setActiveTab('chat')}
            style={{
              display: 'flex',
              alignItems: 'center',
              gap: '12px',
              padding: '12px 16px',
              borderRadius: '12px',
              background: activeTab === 'chat' ? 'rgba(16, 185, 129, 0.15)' : 'transparent',
              color: activeTab === 'chat' ? '#10b981' : 'var(--text-muted)',
              border: activeTab === 'chat' ? '1px solid rgba(16, 185, 129, 0.3)' : '1px solid transparent',
              cursor: 'pointer',
              fontWeight: 600,
              fontSize: '0.95rem',
              textAlign: 'right'
            }}
          >
            <Bot size={20} />
            <span>ميدان المحادثة والصوت</span>
          </button>

          <button
            id="tab-bookings-btn"
            onClick={() => setActiveTab('bookings')}
            style={{
              display: 'flex',
              alignItems: 'center',
              gap: '12px',
              padding: '12px 16px',
              borderRadius: '12px',
              background: activeTab === 'bookings' ? 'rgba(16, 185, 129, 0.15)' : 'transparent',
              color: activeTab === 'bookings' ? '#10b981' : 'var(--text-muted)',
              border: activeTab === 'bookings' ? '1px solid rgba(16, 185, 129, 0.3)' : '1px solid transparent',
              cursor: 'pointer',
              fontWeight: 600,
              fontSize: '0.95rem',
              textAlign: 'right'
            }}
          >
            <Calendar size={20} />
            <span>الحجوزات والمواعيد</span>
            {bookings.length > 0 && (
              <span style={{
                marginRight: 'auto',
                background: '#10b981',
                color: '#000',
                fontSize: '0.75rem',
                padding: '2px 8px',
                borderRadius: '10px',
                fontWeight: 700
              }}>
                {bookings.length}
              </span>
            )}
          </button>

          <button
            id="tab-voice-btn"
            onClick={() => setActiveTab('voice')}
            style={{
              display: 'flex',
              alignItems: 'center',
              gap: '12px',
              padding: '12px 16px',
              borderRadius: '12px',
              background: activeTab === 'voice' ? 'rgba(16, 185, 129, 0.15)' : 'transparent',
              color: activeTab === 'voice' ? '#10b981' : 'var(--text-muted)',
              border: activeTab === 'voice' ? '1px solid rgba(16, 185, 129, 0.3)' : '1px solid transparent',
              cursor: 'pointer',
              fontWeight: 600,
              fontSize: '0.95rem',
              textAlign: 'right'
            }}
          >
            <Mic size={20} />
            <span>المحادثة الصوتية والمقاطعة</span>
          </button>

          <button
            id="tab-knowledge-btn"
            onClick={() => setActiveTab('knowledge')}
            style={{
              display: 'flex',
              alignItems: 'center',
              gap: '12px',
              padding: '12px 16px',
              borderRadius: '12px',
              background: activeTab === 'knowledge' ? 'rgba(16, 185, 129, 0.15)' : 'transparent',
              color: activeTab === 'knowledge' ? '#10b981' : 'var(--text-muted)',
              border: activeTab === 'knowledge' ? '1px solid rgba(16, 185, 129, 0.3)' : '1px solid transparent',
              cursor: 'pointer',
              fontWeight: 600,
              fontSize: '0.95rem',
              textAlign: 'right'
            }}
          >
            <FileText size={20} />
            <span>إدارة المعرفة والـ RAG</span>
            {knowledgeDocs.length > 0 && (
              <span style={{
                marginRight: 'auto',
                background: '#06b6d4',
                color: '#000',
                fontSize: '0.75rem',
                padding: '2px 8px',
                borderRadius: '10px',
                fontWeight: 700
              }}>
                {knowledgeDocs.length}
              </span>
            )}
          </button>

          <button
            id="tab-settings-btn"
            onClick={() => setActiveTab('settings')}
            style={{
              display: 'flex',
              alignItems: 'center',
              gap: '12px',
              padding: '12px 16px',
              borderRadius: '12px',
              background: activeTab === 'settings' ? 'rgba(16, 185, 129, 0.15)' : 'transparent',
              color: activeTab === 'settings' ? '#10b981' : 'var(--text-muted)',
              border: activeTab === 'settings' ? '1px solid rgba(16, 185, 129, 0.3)' : '1px solid transparent',
              cursor: 'pointer',
              fontWeight: 600,
              fontSize: '0.95rem',
              textAlign: 'right'
            }}
          >
            <Settings size={20} />
            <span>إعدادات الوكيل والأدوات</span>
          </button>

          <button
            onClick={() => setActiveTab('logs')}
            style={{
              display: 'flex',
              alignItems: 'center',
              gap: '12px',
              padding: '12px 16px',
              borderRadius: '12px',
              background: activeTab === 'logs' ? 'rgba(16, 185, 129, 0.15)' : 'transparent',
              color: activeTab === 'logs' ? '#10b981' : 'var(--text-muted)',
              border: activeTab === 'logs' ? '1px solid rgba(16, 185, 129, 0.3)' : '1px solid transparent',
              cursor: 'pointer',
              fontWeight: 600,
              fontSize: '0.95rem',
              textAlign: 'right'
            }}
          >
            <FileText size={20} />
            <span>سجل تنفيذ الأدوات</span>
            {toolLogs.length > 0 && (
              <span style={{
                marginRight: 'auto',
                background: 'rgba(255, 255, 255, 0.1)',
                color: '#fff',
                fontSize: '0.75rem',
                padding: '2px 8px',
                borderRadius: '10px'
              }}>
                {toolLogs.length}
              </span>
            )}
          </button>

          {/* Business Hours Info Card */}
          <div style={{
            marginTop: 'auto',
            background: 'var(--bg-glass)',
            border: '1px solid var(--border-color)',
            padding: '16px',
            borderRadius: '16px',
            fontSize: '0.8rem'
          }}>
            <h4 style={{ color: '#fff', marginBottom: '6px', display: 'flex', alignItems: 'center', gap: '6px' }}>
              <ShieldCheck size={16} color="#10b981" />
              ضوابط العمل التشغيلية
            </h4>
            <p style={{ color: 'var(--text-muted)', lineHeight: '1.4' }}>
              • الأحد إلى الخميس (9:00 - 17:00).<br />
              • تأكيد العميل الصريح إلزامي للحجز.<br />
              • حماية التكرار عبر Idempotency Key.<br />
              • قاعدة بيانات ACID متوافقة كلياً.
            </p>
          </div>
        </aside>

        {/* Content Area */}
        <main style={{ flex: 1, display: 'flex', flexDirection: 'column' }}>
          {/* TAB 1: Chat & Voice Playground */}
          {activeTab === 'chat' && (
            <div className="glass-panel" style={{ flex: 1, display: 'flex', flexDirection: 'column', height: 'calc(100vh - 120px)' }}>
              {/* Chat Header */}
              <div style={{
                padding: '16px 20px',
                borderBottom: '1px solid var(--border-color)',
                display: 'flex',
                alignItems: 'center',
                justifyContent: 'space-between'
              }}>
                <div style={{ display: 'flex', alignItems: 'center', gap: '10px' }}>
                  <div style={{
                    width: '10px',
                    height: '10px',
                    borderRadius: '50%',
                    backgroundColor: '#10b981',
                    boxShadow: '0 0 10px #10b981'
                  }} />
                  <span style={{ fontWeight: 700, fontSize: '1rem', color: '#fff' }}>
                    {selectedAgent?.name || 'سارة - مساعدة العيادة'}
                  </span>
                  <span style={{
                    fontSize: '0.75rem',
                    background: 'rgba(16, 185, 129, 0.1)',
                    color: '#10b981',
                    padding: '2px 8px',
                    borderRadius: '6px'
                  }}>
                    اللهجة المصرية (ar-EG)
                  </span>
                </div>

                <button
                  onClick={() => {
                    setMessages([messages[0]]);
                    setConversationId(createUUID());
                    setPendingCard(null);
                  }}
                  style={{
                    display: 'flex',
                    alignItems: 'center',
                    gap: '6px',
                    background: 'rgba(255, 255, 255, 0.05)',
                    border: '1px solid var(--border-color)',
                    padding: '6px 12px',
                    borderRadius: '8px',
                    color: 'var(--text-muted)',
                    cursor: 'pointer',
                    fontSize: '0.8rem'
                  }}
                >
                  <RefreshCw size={14} />
                  <span>بدء محادثة جديدة</span>
                </button>
              </div>

              {/* Messages Scroll Area */}
              <div style={{
                flex: 1,
                overflowY: 'auto',
                padding: '20px',
                display: 'flex',
                flexDirection: 'column',
                gap: '16px'
              }}>
                {messages.map((msg, idx) => (
                  <div
                    key={idx}
                    style={{
                      display: 'flex',
                      flexDirection: 'column',
                      alignItems: msg.role === 'user' ? 'flex-start' : 'flex-end',
                      maxWidth: '85%',
                      alignSelf: msg.role === 'user' ? 'flex-start' : 'flex-end'
                    }}
                  >
                    <div style={{
                      display: 'flex',
                      alignItems: 'center',
                      gap: '6px',
                      marginBottom: '4px',
                      fontSize: '0.75rem',
                      color: 'var(--text-dim)'
                    }}>
                      {msg.role === 'user' ? <User size={12} /> : <Bot size={12} />}
                      <span>{msg.role === 'user' ? 'العميل' : 'سارة (الوكيل)'}</span>
                      <span>•</span>
                      <span>{msg.time}</span>
                    </div>

                    <div style={{
                      padding: '12px 18px',
                      borderRadius: msg.role === 'user' ? '16px 16px 16px 4px' : '16px 16px 4px 16px',
                      background: msg.role === 'user'
                        ? 'linear-gradient(135deg, #10b981 0%, #059669 100%)'
                        : 'rgba(255, 255, 255, 0.06)',
                      color: msg.role === 'user' ? '#fff' : 'var(--text-main)',
                      border: msg.role === 'user' ? 'none' : '1px solid var(--border-color)',
                      fontSize: '0.95rem',
                      lineHeight: '1.6',
                      whiteSpace: 'pre-wrap',
                      boxShadow: msg.role === 'user' ? '0 4px 15px rgba(16, 185, 129, 0.2)' : 'none'
                    }}>
                      {msg.content || (isStreaming && idx === messages.length - 1 ? '...' : '')}
                    </div>
                  </div>
                ))}

                {/* Explicit Customer Confirmation Card (Trust Boundary) */}
                {pendingCard && (
                  <div
                    id="booking-confirmation-card"
                    style={{
                      margin: '8px 0',
                      padding: '16px 20px',
                      borderRadius: '16px',
                      background: pendingCard.status === 'Confirmed'
                        ? 'linear-gradient(135deg, rgba(16, 185, 129, 0.15), rgba(5, 150, 105, 0.1))'
                        : pendingCard.status === 'Invalidated' || pendingCard.status === 'FailedCapacity'
                          ? 'linear-gradient(135deg, rgba(239, 68, 68, 0.15), rgba(185, 28, 28, 0.1))'
                          : 'linear-gradient(135deg, rgba(16, 185, 129, 0.1), rgba(6, 78, 59, 0.2))',
                      border: pendingCard.status === 'Confirmed'
                        ? '1px solid #10b981'
                        : pendingCard.status === 'Invalidated' || pendingCard.status === 'FailedCapacity'
                          ? '1px solid #ef4444'
                          : '1px solid rgba(16, 185, 129, 0.4)',
                      boxShadow: '0 8px 30px rgba(0, 0, 0, 0.3)'
                    }}
                  >
                    <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', marginBottom: '12px' }}>
                      <div style={{ display: 'flex', alignItems: 'center', gap: '8px' }}>
                        <ShieldCheck size={20} color={pendingCard.status === 'Confirmed' ? '#10b981' : pendingCard.status === 'Pending' ? '#34d399' : '#ef4444'} />
                        <span style={{ fontWeight: 700, fontSize: '0.95rem', color: '#fff' }}>
                          {pendingCard.status === 'Confirmed'
                            ? 'تم تثبيت وتأكيد الحجز بنجاح'
                            : pendingCard.status === 'Invalidated'
                              ? 'مسودة حجز ملغاة (تم تعديل البيانات)'
                              : pendingCard.status === 'FailedCapacity'
                                ? 'تعذر التأكيد (الموعد محجوز بالكامل)'
                                : 'مراجعة وتأكيد الحجز (موافقة العميل الصريحة)'}
                        </span>
                      </div>
                      <span style={{
                        fontSize: '0.75rem',
                        padding: '3px 10px',
                        borderRadius: '12px',
                        fontWeight: 600,
                        background: pendingCard.status === 'Confirmed' ? 'rgba(16, 185, 129, 0.2)' : pendingCard.status === 'Pending' ? 'rgba(234, 179, 8, 0.2)' : 'rgba(239, 68, 68, 0.2)',
                        color: pendingCard.status === 'Confirmed' ? '#10b981' : pendingCard.status === 'Pending' ? '#facc15' : '#ef4444'
                      }}>
                        {pendingCard.status === 'Confirmed' ? 'مؤكد في السيستم' : pendingCard.status === 'Pending' ? 'بانتظار موافقتك' : 'غير سارٍ'}
                      </span>
                    </div>

                    <div style={{
                      display: 'grid',
                      gridTemplateColumns: 'repeat(auto-fit, minmax(160px, 1fr))',
                      gap: '10px',
                      background: 'rgba(0, 0, 0, 0.25)',
                      padding: '12px 16px',
                      borderRadius: '12px',
                      marginBottom: '14px',
                      fontSize: '0.85rem'
                    }}>
                      <div>
                        <span style={{ color: 'var(--text-dim)', display: 'block' }}>اسم العميل:</span>
                        <span style={{ color: '#fff', fontWeight: 600 }}>{pendingCard.customerName}</span>
                      </div>
                      <div>
                        <span style={{ color: 'var(--text-dim)', display: 'block' }}>رقم التليفون:</span>
                        <span style={{ color: '#fff', fontWeight: 600 }} dir="ltr">{pendingCard.customerPhone}</span>
                      </div>
                      <div>
                        <span style={{ color: 'var(--text-dim)', display: 'block' }}>الخدمة المطلوبة:</span>
                        <span style={{ color: '#fff', fontWeight: 600 }}>{pendingCard.serviceName}</span>
                      </div>
                      <div>
                        <span style={{ color: 'var(--text-dim)', display: 'block' }}>الموعد:</span>
                        <span style={{ color: '#34d399', fontWeight: 600 }}>{pendingCard.cairoTimeFormatted}</span>
                      </div>
                    </div>

                    {pendingCard.errorMessage && (
                      <div style={{
                        padding: '8px 12px',
                        background: 'rgba(239, 68, 68, 0.15)',
                        border: '1px solid rgba(239, 68, 68, 0.3)',
                        borderRadius: '8px',
                        color: '#f87171',
                        fontSize: '0.82rem',
                        marginBottom: '12px'
                      }}>
                        ⚠️ {pendingCard.errorMessage}
                      </div>
                    )}

                    {pendingCard.status === 'Confirmed' ? (
                      <div style={{
                        display: 'flex',
                        alignItems: 'center',
                        gap: '8px',
                        color: '#10b981',
                        fontSize: '0.88rem',
                        fontWeight: 600
                      }}>
                        <CheckCircle2 size={18} />
                        <span>تم الحجز برقم: <code style={{ color: '#fff' }}>{pendingCard.confirmedBookingId}</code></span>
                      </div>
                    ) : pendingCard.status === 'Pending' ? (
                      <button
                        id="confirm-booking-btn"
                        onClick={handleConfirmBooking}
                        disabled={isConfirming}
                        style={{
                          display: 'flex',
                          alignItems: 'center',
                          justifyContent: 'center',
                          gap: '8px',
                          width: '100%',
                          padding: '12px 20px',
                          background: isConfirming ? 'rgba(16, 185, 129, 0.4)' : 'linear-gradient(135deg, #10b981 0%, #059669 100%)',
                          color: '#fff',
                          border: 'none',
                          borderRadius: '10px',
                          fontWeight: 700,
                          fontSize: '0.95rem',
                          cursor: isConfirming ? 'not-allowed' : 'pointer',
                          boxShadow: '0 4px 15px rgba(16, 185, 129, 0.3)',
                          transition: 'all 0.2s'
                        }}
                      >
                        {isConfirming ? (
                          <>
                            <RefreshCw size={16} />
                            <span>جاري تأكيد وتثبيت الحجز...</span>
                          </>
                        ) : (
                          <>
                            <CheckCircle2 size={18} />
                            <span>أؤكد الحجز نهائياً الآن (تأكيد العميل الصريح)</span>
                          </>
                        )}
                      </button>
                    ) : null}
                  </div>
                )}

                <div ref={messagesEndRef} />
              </div>

              {/* Suggestions Chips */}
              <div style={{
                padding: '8px 20px',
                display: 'flex',
                gap: '8px',
                overflowX: 'auto',
                borderTop: '1px solid var(--border-color)'
              }}>
                {[
                  'إيه المواعيد المتاحة بكرة لكشف الباطنة؟',
                  'عايز أحجز كشف باطنة الساعة 2 الضهر باسم محمد عاطف 01012345678',
                  'أيوة أكد الحجز تمام',
                  'استعلم لي عن حجز باسم محمد عاطف'
                ].map((chip, i) => (
                  <button
                    key={i}
                    onClick={() => handleSendMessage(chip)}
                    disabled={isStreaming}
                    style={{
                      background: 'rgba(255, 255, 255, 0.04)',
                      border: '1px solid var(--border-color)',
                      padding: '6px 12px',
                      borderRadius: '16px',
                      color: 'var(--text-muted)',
                      fontSize: '0.8rem',
                      cursor: 'pointer',
                      whiteSpace: 'nowrap'
                    }}
                  >
                    {chip}
                  </button>
                ))}
              </div>

              {/* Chat Input Bar with Push-to-Talk Microphone */}
              <div style={{
                padding: '16px 20px',
                background: 'rgba(0, 0, 0, 0.2)',
                borderTop: '1px solid var(--border-color)',
                display: 'flex',
                alignItems: 'center',
                gap: '12px'
              }}>
                <button
                  type="button"
                  onClick={toggleRecording}
                  title={isRecording ? 'إيقاف التسجيل' : 'تحدث عبر الميكروفون (Push to Talk)'}
                  style={{
                    background: isRecording ? '#ef4444' : 'rgba(255, 255, 255, 0.08)',
                    border: '1px solid var(--border-color)',
                    borderRadius: '12px',
                    width: '46px',
                    height: '46px',
                    display: 'flex',
                    alignItems: 'center',
                    justifyContent: 'center',
                    cursor: 'pointer',
                    color: '#fff',
                    transition: 'all 0.2s',
                    boxShadow: isRecording ? '0 0 15px #ef4444' : 'none'
                  }}
                  className={isRecording ? 'pulsing' : ''}
                >
                  {isRecording ? <MicOff size={20} /> : <Mic size={20} />}
                </button>

                <input
                  id="chat-input-text"
                  type="text"
                  value={inputMessage}
                  onChange={e => setInputMessage(e.target.value)}
                  onKeyDown={e => e.key === 'Enter' && handleSendMessage()}
                  placeholder="اكتب رسالتك بالعامية المصرية (أو استخدم زر الميكروفون)..."
                  disabled={isStreaming}
                  style={{
                    flex: 1,
                    background: 'rgba(255, 255, 255, 0.05)',
                    border: '1px solid var(--border-color)',
                    borderRadius: '12px',
                    padding: '12px 16px',
                    color: '#fff',
                    fontSize: '0.95rem',
                    outline: 'none',
                    fontFamily: 'inherit'
                  }}
                />

                <button
                  id="chat-send-btn"
                  type="button"
                  onClick={() => handleSendMessage()}
                  disabled={isStreaming || !inputMessage.trim()}
                  style={{
                    background: isStreaming || !inputMessage.trim()
                      ? 'rgba(255, 255, 255, 0.1)'
                      : 'linear-gradient(135deg, #10b981 0%, #059669 100%)',
                    border: 'none',
                    borderRadius: '12px',
                    padding: '0 20px',
                    height: '46px',
                    display: 'flex',
                    alignItems: 'center',
                    gap: '8px',
                    color: '#fff',
                    fontWeight: 600,
                    cursor: isStreaming || !inputMessage.trim() ? 'not-allowed' : 'pointer',
                    boxShadow: isStreaming || !inputMessage.trim() ? 'none' : '0 4px 15px rgba(16, 185, 129, 0.3)'
                  }}
                >
                  <Send size={18} />
                  <span>إرسال</span>
                </button>
              </div>
            </div>
          )}

          {/* TAB 2: Bookings & Slots */}
          {activeTab === 'bookings' && (
            <div style={{ display: 'flex', flexDirection: 'column', gap: '24px' }}>
              {/* Confirmed Bookings Table */}
              <div className="glass-panel" style={{ padding: '24px' }}>
                <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: '16px' }}>
                  <h3 style={{ fontSize: '1.1rem', fontWeight: 700, color: '#fff', display: 'flex', alignItems: 'center', gap: '8px' }}>
                    <Calendar size={18} color="#10b981" />
                    الحجوزات المسجلة المؤكدة (قاعدة بيانات ACID)
                  </h3>
                  <button
                    onClick={() => fetchState()}
                    style={{
                      background: 'rgba(255, 255, 255, 0.05)',
                      border: '1px solid var(--border-color)',
                      color: 'var(--text-muted)',
                      padding: '6px 12px',
                      borderRadius: '8px',
                      fontSize: '0.8rem',
                      cursor: 'pointer',
                      display: 'flex',
                      alignItems: 'center',
                      gap: '6px'
                    }}
                  >
                    <RefreshCw size={14} />
                    <span>تحديث</span>
                  </button>
                </div>

                {bookings.length === 0 ? (
                  <p style={{ color: 'var(--text-muted)', textAlign: 'center', padding: '32px' }}>
                    لا توجد حجوزات مسجلة حتى الآن. جرب إجراء محادثة مع الوكيل لحجز موعد.
                  </p>
                ) : (
                  <div style={{ overflowX: 'auto' }}>
                    <table style={{ width: '100%', borderCollapse: 'collapse', textAlign: 'right' }}>
                      <thead>
                        <tr style={{ borderBottom: '1px solid var(--border-color)', color: 'var(--text-muted)', fontSize: '0.85rem' }}>
                          <th style={{ padding: '12px' }}>اسم العميل</th>
                          <th style={{ padding: '12px' }}>رقم التليفون</th>
                          <th style={{ padding: '12px' }}>الخدمة</th>
                          <th style={{ padding: '12px' }}>موعد الكشف (بتوقيت القاهرة)</th>
                          <th style={{ padding: '12px' }}>الحالة</th>
                          <th style={{ padding: '12px' }}>مفتاح Idempotency</th>
                        </tr>
                      </thead>
                      <tbody>
                        {bookings.map(b => (
                          <tr key={b.id} style={{ borderBottom: '1px solid rgba(255, 255, 255, 0.04)', fontSize: '0.9rem' }}>
                            <td style={{ padding: '12px', fontWeight: 600, color: '#fff' }}>{b.customerName}</td>
                            <td style={{ padding: '12px' }} dir="ltr">{b.customerPhone}</td>
                            <td style={{ padding: '12px' }}>{b.serviceName}</td>
                            <td style={{ padding: '12px', color: '#10b981' }}>{b.cairoTimeFormatted}</td>
                            <td style={{ padding: '12px' }}>
                              <span style={{
                                background: 'rgba(16, 185, 129, 0.1)',
                                color: '#10b981',
                                padding: '4px 8px',
                                borderRadius: '6px',
                                fontSize: '0.75rem',
                                fontWeight: 600
                              }}>
                                {b.status}
                              </span>
                            </td>
                            <td style={{ padding: '12px', color: 'var(--text-dim)', fontSize: '0.75rem' }} dir="ltr">{b.idempotencyKey}</td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </div>
                )}
              </div>

              {/* Available Slots Table */}
              <div className="glass-panel" style={{ padding: '24px' }}>
                <h3 style={{ fontSize: '1.1rem', fontWeight: 700, color: '#fff', marginBottom: '16px', display: 'flex', alignItems: 'center', gap: '8px' }}>
                  <Clock size={18} color="#06b6d4" />
                  المواعيد المتاحة بجدول العيادة (خلال ساعات العمل الرسمية)
                </h3>
                <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(280px, 1fr))', gap: '16px' }}>
                  {slots.map(s => (
                    <div
                      key={s.id}
                      style={{
                        background: 'rgba(255, 255, 255, 0.03)',
                        border: '1px solid var(--border-color)',
                        borderRadius: '12px',
                        padding: '16px',
                        display: 'flex',
                        flexDirection: 'column',
                        gap: '8px'
                      }}
                    >
                      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
                        <span style={{ fontWeight: 600, color: '#fff' }}>{s.serviceName}</span>
                        <span style={{
                          background: s.isAvailable ? 'rgba(16, 185, 129, 0.1)' : 'rgba(239, 68, 68, 0.1)',
                          color: s.isAvailable ? '#10b981' : '#ef4444',
                          padding: '2px 8px',
                          borderRadius: '6px',
                          fontSize: '0.75rem'
                        }}>
                          {s.isAvailable ? 'متاح للحجز' : 'مكتمل بالكامل'}
                        </span>
                      </div>
                      <p style={{ fontSize: '0.85rem', color: '#06b6d4' }}>{s.cairoTimeFormatted}</p>
                      <div style={{ fontSize: '0.75rem', color: 'var(--text-dim)', marginTop: 'auto' }}>
                        السعة المحجوزة: {s.bookedCapacity} من {s.totalCapacity}
                      </div>
                    </div>
                  ))}
                </div>
              </div>
            </div>
          )}

          {/* TAB: Voice Conversation & Barge-in Playground */}
          {activeTab === 'voice' && (
            <div className="glass-panel" style={{ padding: '24px', display: 'flex', flexDirection: 'column', gap: '24px' }}>
              <audio ref={audioPlayerRef} style={{ display: 'none' }} />

              <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
                <div>
                  <h3 style={{ fontSize: '1.25rem', fontWeight: 700, color: '#fff', display: 'flex', alignItems: 'center', gap: '10px' }}>
                    <Mic size={22} color="#10b981" />
                    المحادثة الصوتية الحية واختبار المقاطعة (Voice & Barge-In Playground)
                  </h3>
                  <p style={{ color: 'var(--text-muted)', fontSize: '0.85rem', marginTop: '4px' }}>
                    دورة المحادثة الصوتية الكاملة (STT ➔ LLM ➔ TTS) مع القطع اللحظي للاستجابة القديمة عند المقاطعة.
                  </p>
                </div>

                <div style={{ display: 'flex', gap: '10px' }}>
                  <button
                    id="start-voice-session-btn"
                    onClick={handleStartVoiceSession}
                    style={{
                      background: 'rgba(255, 255, 255, 0.08)',
                      border: '1px solid var(--border-color)',
                      color: '#fff',
                      padding: '8px 16px',
                      borderRadius: '8px',
                      cursor: 'pointer',
                      fontSize: '0.85rem',
                      display: 'flex',
                      alignItems: 'center',
                      gap: '6px'
                    }}
                  >
                    <RefreshCw size={14} />
                    <span>{voiceSessionId ? 'تجديد الجلسة' : 'بدء جلسة صوتية'}</span>
                  </button>

                  <button
                    id="voice-barge-in-btn"
                    onClick={handleBargeInInterrupt}
                    disabled={!voiceSessionId}
                    style={{
                      background: isVoiceSpeaking ? '#ef4444' : 'rgba(239, 68, 68, 0.2)',
                      border: '1px solid #ef4444',
                      color: '#fff',
                      padding: '8px 18px',
                      borderRadius: '8px',
                      cursor: voiceSessionId ? 'pointer' : 'not-allowed',
                      fontWeight: 700,
                      fontSize: '0.85rem',
                      display: 'flex',
                      alignItems: 'center',
                      gap: '8px',
                      boxShadow: isVoiceSpeaking ? '0 0 15px rgba(239, 68, 68, 0.5)' : 'none'
                    }}
                  >
                    <Square size={14} fill="#fff" />
                    <span>مقاطعة فورية (Barge-In)</span>
                  </button>
                </div>
              </div>

              {/* Session State Banner */}
              <div style={{
                background: 'rgba(255, 255, 255, 0.03)',
                border: '1px solid var(--border-color)',
                borderRadius: '12px',
                padding: '16px',
                display: 'flex',
                justifyContent: 'space-between',
                alignItems: 'center',
                flexWrap: 'wrap',
                gap: '12px'
              }}>
                <div style={{ display: 'flex', alignItems: 'center', gap: '16px' }}>
                  <div>
                    <span style={{ fontSize: '0.75rem', color: 'var(--text-dim)', display: 'block' }}>معرف الجلسة</span>
                    <span style={{ fontFamily: 'monospace', fontSize: '0.85rem', color: '#06b6d4' }}>
                      {voiceSessionId ? `${voiceSessionId.substring(0, 18)}...` : 'غير متصل'}
                    </span>
                  </div>

                  <div>
                    <span style={{ fontSize: '0.75rem', color: 'var(--text-dim)', display: 'block' }}>رقم الدور (Turn Epoch)</span>
                    <span id="voice-turn-id-display" style={{ fontWeight: 700, fontSize: '0.85rem', color: '#10b981' }}>
                      #{voiceTurnId}
                    </span>
                  </div>
                </div>

                <div style={{ display: 'flex', alignItems: 'center', gap: '10px' }}>
                  {isVoiceSpeaking && (
                    <span id="voice-speaking-indicator" style={{
                      background: 'rgba(16, 185, 129, 0.2)',
                      border: '1px solid #10b981',
                      color: '#10b981',
                      padding: '4px 12px',
                      borderRadius: '20px',
                      fontSize: '0.8rem',
                      fontWeight: 600,
                      display: 'flex',
                      alignItems: 'center',
                      gap: '6px'
                    }}>
                      <Volume2 size={14} />
                      صوت الوكيل يعمل حالياً (Playing TTS)
                    </span>
                  )}

                  {isVoiceProcessing && (
                    <span id="voice-processing-indicator" style={{
                      background: 'rgba(245, 158, 11, 0.2)',
                      border: '1px solid #f59e0b',
                      color: '#f59e0b',
                      padding: '4px 12px',
                      borderRadius: '20px',
                      fontSize: '0.8rem',
                      fontWeight: 600
                    }}>
                      جاري المعالجة والتوليد...
                    </span>
                  )}

                  {voiceInterrupted && (
                    <span id="voice-interrupted-indicator" style={{
                      background: 'rgba(239, 68, 68, 0.2)',
                      border: '1px solid #ef4444',
                      color: '#ef4444',
                      padding: '4px 12px',
                      borderRadius: '20px',
                      fontSize: '0.8rem',
                      fontWeight: 600
                    }}>
                      تمت المقاطعة وإلغاء الاستجابة القديمة
                    </span>
                  )}
                </div>
              </div>

              {/* Sample Voice Utterances */}
              <div>
                <span style={{ fontSize: '0.85rem', color: 'var(--text-muted)', marginBottom: '8px', display: 'block' }}>
                  جُمَل اختبار سريعة للهجة المصرية (انقر للإرسال والاستماع الفوري):
                </span>
                <div style={{ display: 'flex', flexWrap: 'wrap', gap: '8px' }}>
                  {[
                    'عايز أعرف إيه المواعيد المتاحة لكشف الباطنة؟',
                    'هو كشف الباطنة بكام عندكم؟',
                    'احجزلي ميعاد بكرة باسم محمد عاطف وتليفوني 01012345678'
                  ].map((phrase, i) => (
                    <button
                      key={i}
                      onClick={() => handleSendVoiceTurn(phrase)}
                      disabled={isVoiceProcessing}
                      style={{
                        background: 'rgba(255, 255, 255, 0.05)',
                        border: '1px solid var(--border-color)',
                        color: '#f3f4f6',
                        padding: '8px 14px',
                        borderRadius: '8px',
                        cursor: 'pointer',
                        fontSize: '0.8rem',
                        transition: 'all 0.2s'
                      }}
                    >
                      🗣️ "{phrase}"
                    </button>
                  ))}
                </div>
              </div>

              {/* Voice Turn Input Box */}
              <div style={{
                background: 'rgba(0, 0, 0, 0.25)',
                border: '1px solid var(--border-color)',
                borderRadius: '12px',
                padding: '16px',
                display: 'flex',
                gap: '12px'
              }}>
                <input
                  id="voice-input-text"
                  type="text"
                  placeholder="اكتب رسالة صوتية أو استفسار باللهجة المصرية..."
                  value={voiceText}
                  onChange={e => setVoiceText(e.target.value)}
                  onKeyDown={e => e.key === 'Enter' && handleSendVoiceTurn()}
                  disabled={isVoiceProcessing}
                  style={{
                    flex: 1,
                    background: 'rgba(255, 255, 255, 0.05)',
                    border: '1px solid var(--border-color)',
                    borderRadius: '8px',
                    padding: '12px 16px',
                    color: '#fff',
                    fontSize: '0.9rem',
                    fontFamily: 'inherit'
                  }}
                />
                <button
                  id="send-voice-turn-btn"
                  onClick={() => handleSendVoiceTurn()}
                  disabled={isVoiceProcessing || !voiceText.trim()}
                  style={{
                    background: 'linear-gradient(135deg, #10b981 0%, #059669 100%)',
                    border: 'none',
                    borderRadius: '8px',
                    padding: '0 20px',
                    color: '#fff',
                    fontWeight: 700,
                    cursor: (isVoiceProcessing || !voiceText.trim()) ? 'not-allowed' : 'pointer',
                    display: 'flex',
                    alignItems: 'center',
                    gap: '8px',
                    opacity: (isVoiceProcessing || !voiceText.trim()) ? 0.6 : 1
                  }}
                >
                  <Send size={16} />
                  <span>توليد وتحدث</span>
                </button>
              </div>

              {/* Active Voice Response Content Display */}
              {voiceResponseText && (
                <div
                  id="voice-response-content-display"
                  style={{
                    background: 'rgba(16, 185, 129, 0.08)',
                    border: '1px solid rgba(16, 185, 129, 0.3)',
                    borderRadius: '12px',
                    padding: '16px',
                    color: '#f3f4f6',
                    fontSize: '0.9rem',
                    lineHeight: '1.6'
                  }}
                >
                  <div style={{ color: '#10b981', fontWeight: 600, fontSize: '0.8rem', marginBottom: '6px' }}>
                    رد الوكيل الصوتي:
                  </div>
                  {voiceResponseText}
                </div>
              )}

              {/* Technical Specifications */}
              <div style={{
                display: 'grid',
                gridTemplateColumns: 'repeat(auto-fit, minmax(220px, 1fr))',
                gap: '14px',
                marginTop: '8px'
              }}>
                <div style={{ background: 'rgba(255, 255, 255, 0.02)', padding: '12px', borderRadius: '8px', border: '1px solid var(--border-color)' }}>
                  <div style={{ color: '#10b981', fontWeight: 600, fontSize: '0.8rem', marginBottom: '4px' }}>التعرف على الصوت (STT)</div>
                  <div style={{ color: 'var(--text-muted)', fontSize: '0.75rem' }}>Whisper Base مع معايرة اللهجة المصرية</div>
                </div>
                <div style={{ background: 'rgba(255, 255, 255, 0.02)', padding: '12px', borderRadius: '8px', border: '1px solid var(--border-color)' }}>
                  <div style={{ color: '#06b6d4', fontWeight: 600, fontSize: '0.8rem', marginBottom: '4px' }}>التوليد الصوتي (TTS)</div>
                  <div style={{ color: 'var(--text-muted)', fontSize: '0.75rem' }}>16kHz 16-bit Mono RIFF WAV مصري</div>
                </div>
                <div style={{ background: 'rgba(255, 255, 255, 0.02)', padding: '12px', borderRadius: '8px', border: '1px solid var(--border-color)' }}>
                  <div style={{ color: '#f59e0b', fontWeight: 600, fontSize: '0.8rem', marginBottom: '4px' }}>بروتوكول المقاطعة (Barge-in)</div>
                  <div style={{ color: 'var(--text-muted)', fontSize: '0.75rem' }}>إلغاء رمزي فوري + تصفير المشغل الصوتي</div>
                </div>
              </div>
            </div>
          )}

          {/* TAB: Knowledge Management & RAG */}
          {activeTab === 'knowledge' && (
            <div className="glass-panel" style={{ padding: '24px', display: 'flex', flexDirection: 'column', gap: '24px' }}>
              <div>
                <h3 style={{ fontSize: '1.25rem', fontWeight: 700, color: '#fff', display: 'flex', alignItems: 'center', gap: '10px' }}>
                  <FileText size={22} color="#06b6d4" />
                  إدارة قاعدة المعرفة والـ RAG (pgvector Semantic Search)
                </h3>
                <p style={{ color: 'var(--text-muted)', fontSize: '0.85rem', marginTop: '4px' }}>
                  تقطيع الوثائق وتخزين المتجهات دلالياً، مع حماية صارمة ضد حقن التعليمات الخبيثة (Prompt Injection Defense).
                </p>
              </div>

              <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: '20px' }}>
                {/* Ingestion Form */}
                <div style={{
                  background: 'rgba(255, 255, 255, 0.03)',
                  border: '1px solid var(--border-color)',
                  borderRadius: '12px',
                  padding: '18px'
                }}>
                  <h4 style={{ color: '#fff', fontSize: '0.95rem', fontWeight: 600, marginBottom: '14px', display: 'flex', alignItems: 'center', gap: '8px' }}>
                    <UploadCloud size={16} color="#10b981" />
                    إضافة وثيقة معرفية جديدة
                  </h4>

                  <form onSubmit={handleIngestDoc} style={{ display: 'flex', flexDirection: 'column', gap: '12px' }}>
                    <div>
                      <label style={{ display: 'block', fontSize: '0.75rem', color: 'var(--text-muted)', marginBottom: '4px' }}>
                        عنوان الوثيقة
                      </label>
                      <input
                        type="text"
                        placeholder="مثال: أسعار كشف العيادة والخدمات"
                        value={newDocTitle}
                        onChange={e => setNewDocTitle(e.target.value)}
                        required
                        style={{
                          width: '100%',
                          background: 'rgba(255, 255, 255, 0.05)',
                          border: '1px solid var(--border-color)',
                          borderRadius: '8px',
                          padding: '8px 12px',
                          color: '#fff',
                          fontSize: '0.85rem',
                          fontFamily: 'inherit'
                        }}
                      />
                    </div>

                    <div>
                      <label style={{ display: 'block', fontSize: '0.75rem', color: 'var(--text-muted)', marginBottom: '4px' }}>
                        التصنيف
                      </label>
                      <select
                        value={newDocCategory}
                        onChange={e => setNewDocCategory(e.target.value)}
                        style={{
                          width: '100%',
                          background: '#111827',
                          border: '1px solid var(--border-color)',
                          borderRadius: '8px',
                          padding: '8px 12px',
                          color: '#fff',
                          fontSize: '0.85rem',
                          fontFamily: 'inherit'
                        }}
                      >
                        <option value="Services">الخدمات والأسعار (Services)</option>
                        <option value="Doctors">الأطباء والمواعيد (Doctors)</option>
                        <option value="Policies">تعليمات وسياسات العيادة (Policies)</option>
                        <option value="FAQ">الأسئلة الشائعة (FAQ)</option>
                      </select>
                    </div>

                    <div>
                      <label style={{ display: 'block', fontSize: '0.75rem', color: 'var(--text-muted)', marginBottom: '4px' }}>
                        محتوى الوثيقة (نصي / Markdown)
                      </label>
                      <textarea
                        rows={6}
                        placeholder="أدخل تفاصيل الوثيقة هنا. يتم التقطيع وتوليد المتجهات تلقائياً..."
                        value={newDocContent}
                        onChange={e => setNewDocContent(e.target.value)}
                        required
                        style={{
                          width: '100%',
                          background: 'rgba(255, 255, 255, 0.05)',
                          border: '1px solid var(--border-color)',
                          borderRadius: '8px',
                          padding: '10px',
                          color: '#fff',
                          fontSize: '0.85rem',
                          lineHeight: '1.5',
                          fontFamily: 'inherit'
                        }}
                      />
                    </div>

                    <button
                      type="submit"
                      disabled={isIngesting || !newDocTitle.trim() || !newDocContent.trim()}
                      style={{
                        background: 'linear-gradient(135deg, #10b981 0%, #059669 100%)',
                        border: 'none',
                        borderRadius: '8px',
                        padding: '10px',
                        color: '#fff',
                        fontWeight: 700,
                        cursor: isIngesting ? 'not-allowed' : 'pointer',
                        fontSize: '0.85rem',
                        marginTop: '4px'
                      }}
                    >
                      {isIngesting ? 'جاري الفهرسة والتضمين...' : 'فهرسة وتضمين الوثيقة'}
                    </button>
                  </form>
                </div>

                {/* Semantic Search Tester */}
                <div style={{
                  background: 'rgba(255, 255, 255, 0.03)',
                  border: '1px solid var(--border-color)',
                  borderRadius: '12px',
                  padding: '18px',
                  display: 'flex',
                  flexDirection: 'column'
                }}>
                  <h4 style={{ color: '#fff', fontSize: '0.95rem', fontWeight: 600, marginBottom: '14px', display: 'flex', alignItems: 'center', gap: '8px' }}>
                    <Search size={16} color="#06b6d4" />
                    اختبار الاسترجاع الدلالي (Semantic Search Test)
                  </h4>

                  <form onSubmit={handleSearchRag} style={{ display: 'flex', gap: '8px', marginBottom: '14px' }}>
                    <input
                      type="text"
                      placeholder="استعلام دلالي، مثلاً: كشف الباطنة بكام؟"
                      value={ragQuery}
                      onChange={e => setRagQuery(e.target.value)}
                      style={{
                        flex: 1,
                        background: 'rgba(255, 255, 255, 0.05)',
                        border: '1px solid var(--border-color)',
                        borderRadius: '8px',
                        padding: '8px 12px',
                        color: '#fff',
                        fontSize: '0.85rem',
                        fontFamily: 'inherit'
                      }}
                    />
                    <button
                      type="submit"
                      disabled={isSearchingRag || !ragQuery.trim()}
                      style={{
                        background: 'rgba(6, 182, 212, 0.2)',
                        border: '1px solid #06b6d4',
                        color: '#06b6d4',
                        borderRadius: '8px',
                        padding: '8px 16px',
                        cursor: isSearchingRag ? 'not-allowed' : 'pointer',
                        fontSize: '0.85rem',
                        fontWeight: 600
                      }}
                    >
                      {isSearchingRag ? 'بحث...' : 'بحث دلالي'}
                    </button>
                  </form>

                  <div style={{ flex: 1, overflowY: 'auto', maxHeight: '280px', display: 'flex', flexDirection: 'column', gap: '8px' }}>
                    {ragResults.length === 0 ? (
                      <p style={{ color: 'var(--text-dim)', fontSize: '0.8rem', textAlign: 'center', margin: 'auto' }}>
                        أدخل استعلاماً واضغط "بحث دلالي" لرؤية المقاطع الأكثر تطابقاً ودرجة التشابه.
                      </p>
                    ) : (
                      ragResults.map((r, i) => (
                        <div key={i} style={{
                          background: 'rgba(0, 0, 0, 0.25)',
                          border: '1px solid var(--border-color)',
                          borderRadius: '8px',
                          padding: '10px',
                          fontSize: '0.8rem'
                        }}>
                          <div style={{ display: 'flex', justifyContent: 'space-between', marginBottom: '4px' }}>
                            <span style={{ fontWeight: 600, color: '#06b6d4' }}>{r.documentTitle}</span>
                            <span style={{ color: '#10b981', fontSize: '0.75rem', fontWeight: 600 }}>
                              التطابق: {(r.score * 100).toFixed(1)}%
                            </span>
                          </div>
                          <p style={{ color: '#d1d5db', lineHeight: '1.4' }}>{r.chunkContent}</p>
                        </div>
                      ))
                    )}
                  </div>
                </div>
              </div>

              {/* Ingested Documents List */}
              <div style={{
                background: 'rgba(255, 255, 255, 0.03)',
                border: '1px solid var(--border-color)',
                borderRadius: '12px',
                padding: '18px'
              }}>
                <h4 style={{ color: '#fff', fontSize: '0.95rem', fontWeight: 600, marginBottom: '14px', display: 'flex', alignItems: 'center', gap: '8px' }}>
                  <BookOpen size={16} color="#10b981" />
                  قائمة الوثائق المفهرسة في قاعدة البيانات ({knowledgeDocs.length})
                </h4>

                {knowledgeDocs.length === 0 ? (
                  <p style={{ color: 'var(--text-muted)', fontSize: '0.85rem', textAlign: 'center', padding: '16px' }}>
                    لا توجد وثائق مفهرسة بعد. يمكنك إضافة وثيقة أعلاه.
                  </p>
                ) : (
                  <table style={{ width: '100%', borderCollapse: 'collapse', textAlign: 'right', fontSize: '0.85rem' }}>
                    <thead>
                      <tr style={{ borderBottom: '1px solid var(--border-color)', color: 'var(--text-muted)' }}>
                        <th style={{ padding: '8px' }}>العنوان</th>
                        <th style={{ padding: '8px' }}>التصنيف</th>
                        <th style={{ padding: '8px' }}>الملف</th>
                        <th style={{ padding: '8px' }}>عدد المقاطع</th>
                        <th style={{ padding: '8px' }}>تاريخ الإدخال</th>
                      </tr>
                    </thead>
                    <tbody>
                      {knowledgeDocs.map((doc) => (
                        <tr key={doc.id} style={{ borderBottom: '1px solid rgba(255, 255, 255, 0.05)' }}>
                          <td style={{ padding: '10px 8px', fontWeight: 600, color: '#fff' }}>{doc.title}</td>
                          <td style={{ padding: '10px 8px', color: '#06b6d4' }}>{doc.category}</td>
                          <td style={{ padding: '10px 8px', color: 'var(--text-dim)', fontFamily: 'monospace' }}>{doc.fileName}</td>
                          <td style={{ padding: '10px 8px', color: '#10b981', fontWeight: 600 }}>{doc.chunkCount} مقطع</td>
                          <td style={{ padding: '10px 8px', color: 'var(--text-dim)', fontSize: '0.8rem' }}>
                            {new Date(doc.createdAtUtc).toLocaleDateString('ar-EG')}
                          </td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                )}
              </div>
            </div>
          )}

          {/* TAB 3: Agent Settings */}
          {activeTab === 'settings' && (
            <div className="glass-panel" style={{ padding: '24px', maxWidth: '800px' }}>
              <h3 style={{ fontSize: '1.25rem', fontWeight: 700, color: '#fff', marginBottom: '16px', display: 'flex', alignItems: 'center', gap: '8px' }}>
                <Settings size={20} color="#10b981" />
                إعدادات الوكيل وقواعد العمل
              </h3>

              {saveSuccess && (
                <div style={{
                  background: 'rgba(16, 185, 129, 0.15)',
                  border: '1px solid #10b981',
                  color: '#10b981',
                  padding: '12px 16px',
                  borderRadius: '10px',
                  marginBottom: '16px',
                  display: 'flex',
                  alignItems: 'center',
                  gap: '8px'
                }}>
                  <CheckCircle2 size={18} />
                  <span>تم حفظ إعدادات الوكيل بنجاح!</span>
                </div>
              )}

              <form onSubmit={handleSaveSettings} style={{ display: 'flex', flexDirection: 'column', gap: '20px' }}>
                <div>
                  <label style={{ display: 'block', fontSize: '0.85rem', color: 'var(--text-muted)', marginBottom: '8px' }}>
                    اسم الوكيل
                  </label>
                  <input
                    id="agent-name-input"
                    type="text"
                    value={agentForm.name || ''}
                    onChange={e => setAgentForm({ ...agentForm, name: e.target.value })}
                    style={{
                      width: '100%',
                      background: 'rgba(255, 255, 255, 0.05)',
                      border: '1px solid var(--border-color)',
                      borderRadius: '10px',
                      padding: '10px 14px',
                      color: '#fff',
                      fontFamily: 'inherit'
                    }}
                  />
                </div>

                <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: '16px' }}>
                  <div>
                    <label style={{ display: 'block', fontSize: '0.85rem', color: 'var(--text-muted)', marginBottom: '8px' }}>
                      النموذج اللغوي (Model)
                    </label>
                    <select
                      value={agentForm.modelName || 'qwen2.5:1.5b'}
                      onChange={e => setAgentForm({ ...agentForm, modelName: e.target.value })}
                      style={{
                        width: '100%',
                        background: '#111827',
                        border: '1px solid var(--border-color)',
                        borderRadius: '10px',
                        padding: '10px 14px',
                        color: '#fff',
                        fontFamily: 'inherit'
                      }}
                    >
                      <option value="qwen2.5:1.5b">qwen2.5:1.5b (Ollama محلي سريع)</option>
                      <option value="qwen2.5:3b">qwen2.5:3b (Ollama محلي أعلى دقة)</option>
                      <option value="deterministic">DeterministicFake (اختبار فوري بدون أخطاء)</option>
                    </select>
                  </div>

                  <div>
                    <label style={{ display: 'block', fontSize: '0.85rem', color: 'var(--text-muted)', marginBottom: '8px' }}>
                      درجة الإبداع (Temperature): {agentForm.temperature ?? 0.2}
                    </label>
                    <input
                      type="range"
                      min="0"
                      max="1"
                      step="0.05"
                      value={agentForm.temperature ?? 0.2}
                      onChange={e => setAgentForm({ ...agentForm, temperature: parseFloat(e.target.value) })}
                      style={{ width: '100%', marginTop: '10px' }}
                    />
                  </div>
                </div>

                <div>
                  <label style={{ display: 'block', fontSize: '0.85rem', color: 'var(--text-muted)', marginBottom: '8px' }}>
                    تعليمات وسلوك الوكيل (System Prompt بالعامية المصرية)
                  </label>
                  <textarea
                    rows={8}
                    value={agentForm.systemPrompt || ''}
                    onChange={e => setAgentForm({ ...agentForm, systemPrompt: e.target.value })}
                    style={{
                      width: '100%',
                      background: 'rgba(255, 255, 255, 0.05)',
                      border: '1px solid var(--border-color)',
                      borderRadius: '10px',
                      padding: '12px',
                      color: '#fff',
                      lineHeight: '1.6',
                      fontFamily: 'inherit',
                      fontSize: '0.9rem'
                    }}
                  />
                </div>

                <div>
                  <label style={{ display: 'block', fontSize: '0.85rem', color: 'var(--text-muted)', marginBottom: '10px' }}>
                    قائمة الأدوات المسموح بها لهذا الوكيل (Tool Allowlist)
                  </label>
                  <div style={{ display: 'flex', gap: '16px', flexWrap: 'wrap' }}>
                    {['CheckAvailability', 'StageBooking', 'GetBooking', 'SearchKnowledgeBase'].map(tool => {
                      const isAllowed = agentForm.allowedTools?.includes(tool);
                      return (
                        <label
                          key={tool}
                          style={{
                            display: 'flex',
                            alignItems: 'center',
                            gap: '8px',
                            background: isAllowed ? 'rgba(16, 185, 129, 0.1)' : 'rgba(255, 255, 255, 0.03)',
                            padding: '8px 14px',
                            borderRadius: '8px',
                            border: isAllowed ? '1px solid #10b981' : '1px solid var(--border-color)',
                            cursor: 'pointer',
                            fontSize: '0.85rem'
                          }}
                        >
                          <input
                            type="checkbox"
                            checked={isAllowed}
                            onChange={e => {
                              const current = agentForm.allowedTools || [];
                              const updated = e.target.checked
                                ? [...current, tool]
                                : current.filter(t => t !== tool);
                              setAgentForm({ ...agentForm, allowedTools: updated });
                            }}
                          />
                          <span style={{ color: isAllowed ? '#10b981' : '#fff' }}>{tool}</span>
                        </label>
                      );
                    })}
                  </div>
                </div>

                <button
                  id="save-settings-btn"
                  type="submit"
                  style={{
                    background: 'linear-gradient(135deg, #10b981 0%, #059669 100%)',
                    border: 'none',
                    borderRadius: '10px',
                    padding: '12px 24px',
                    color: '#fff',
                    fontWeight: 700,
                    cursor: 'pointer',
                    alignSelf: 'flex-start',
                    marginTop: '10px'
                  }}
                >
                  حفظ وتطبيق التعديلات
                </button>
              </form>
            </div>
          )}

          {/* TAB 4: Tool Execution Logs */}
          {activeTab === 'logs' && (
            <div className="glass-panel" style={{ padding: '24px' }}>
              <h3 style={{ fontSize: '1.25rem', fontWeight: 700, color: '#fff', marginBottom: '16px', display: 'flex', alignItems: 'center', gap: '8px' }}>
                <FileText size={20} color="#06b6d4" />
                سجل تنفيذ الأدوات ومراقبة العمليات (Audit Logs)
              </h3>

              {toolLogs.length === 0 ? (
                <p style={{ color: 'var(--text-muted)', textAlign: 'center', padding: '32px' }}>
                  لم يتم تنفيذ أدوات في جلسة المحادثة الحالية حتى الآن.
                </p>
              ) : (
                <div style={{ display: 'flex', flexDirection: 'column', gap: '12px' }}>
                  {toolLogs.map((log, i) => (
                    <div
                      key={i}
                      style={{
                        background: 'rgba(255, 255, 255, 0.03)',
                        border: '1px solid var(--border-color)',
                        borderRadius: '10px',
                        padding: '14px',
                        fontSize: '0.85rem'
                      }}
                    >
                      <div style={{ display: 'flex', justifyContent: 'space-between', marginBottom: '8px' }}>
                        <span style={{ fontWeight: 700, color: log.type === 'call' ? '#f59e0b' : '#10b981' }}>
                          {log.type === 'call' ? '⚡ استدعاء أداة:' : '✓ نتيجة تنفيذ:'} {log.tool}
                        </span>
                        <span style={{ color: 'var(--text-dim)', fontSize: '0.75rem' }}>{log.time}</span>
                      </div>
                      <pre style={{
                        background: 'rgba(0, 0, 0, 0.3)',
                        padding: '10px',
                        borderRadius: '8px',
                        overflowX: 'auto',
                        fontSize: '0.8rem',
                        color: '#93c5fd'
                      }}>
                        {JSON.stringify(log.meta, null, 2)}
                      </pre>
                    </div>
                  ))}
                </div>
              )}
            </div>
          )}
        </main>
      </div>
    </div>
  );
}
