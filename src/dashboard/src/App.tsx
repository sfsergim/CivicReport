import { useEffect, useMemo, useState } from 'react';
import { MapContainer, Marker, Popup, TileLayer } from 'react-leaflet';
import L from 'leaflet';

type Report = {
  id: string;
  category: string;
  description: string;
  lat: number;
  lng: number;
  accuracyMeters: number;
  createdAt: string;
  validatedAt?: string | null;
  status: string;
  publicPhotoUrl: string;
};

const apiUrl = import.meta.env.VITE_API_URL || 'http://localhost:5000';

const markerIcon = new L.Icon({
  iconUrl: 'https://unpkg.com/leaflet@1.9.4/dist/images/marker-icon.png',
  iconSize: [25, 41],
  iconAnchor: [12, 41]
});

export default function App() {
  const [token, setToken] = useState(() => localStorage.getItem('token') || '');
  const [phone, setPhone] = useState('');
  const [otp, setOtp] = useState('');
  const [message, setMessage] = useState('');
  const [reports, setReports] = useState<Report[]>([]);
  const [category, setCategory] = useState('');
  const [status, setStatus] = useState('');
  const [from, setFrom] = useState('');
  const [to, setTo] = useState('');

  const headers = useMemo(() => ({
    'Content-Type': 'application/json',
    Authorization: `Bearer ${token}`
  }), [token]);

  useEffect(() => {
    if (!token) {
      return;
    }

    const params = new URLSearchParams();
    if (category) params.append('category', category);
    if (status) params.append('status', status);
    if (from) params.append('from', new Date(from).toISOString());
    if (to) params.append('to', new Date(to).toISOString());

    fetch(`${apiUrl}/admin/reports?${params.toString()}`, { headers })
      .then(async (res) => {
        if (!res.ok) {
          throw new Error('Falha ao carregar reports');
        }
        return res.json();
      })
      .then((data) => setReports(data))
      .catch((err) => setMessage(err.message));
  }, [token, category, status, from, to, headers]);

  const requestOtp = async () => {
    setMessage('');
    const res = await fetch(`${apiUrl}/auth/request-otp`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ phone })
    });

    if (res.ok) {
      const data = await res.json();
      setMessage(data.otp_code ? `OTP: ${data.otp_code}` : 'OTP enviado.');
    } else {
      setMessage('Erro ao solicitar OTP.');
    }
  };

  const verifyOtp = async () => {
    setMessage('');
    const res = await fetch(`${apiUrl}/auth/verify-otp`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ phone, otp })
    });

    if (res.ok) {
      const data = await res.json();
      localStorage.setItem('token', data.token);
      setToken(data.token);
      setMessage('Autenticado.');
    } else {
      setMessage('OTP inválido.');
    }
  };

  const exportCsv = async () => {
    const params = new URLSearchParams();
    if (category) params.append('category', category);
    if (status) params.append('status', status);
    if (from) params.append('from', new Date(from).toISOString());
    if (to) params.append('to', new Date(to).toISOString());

    const res = await fetch(`${apiUrl}/admin/reports/export.csv?${params.toString()}`, { headers });
    if (res.ok) {
      const blob = await res.blob();
      const url = URL.createObjectURL(blob);
      const link = document.createElement('a');
      link.href = url;
      link.download = 'reports.csv';
      link.click();
      URL.revokeObjectURL(url);
    } else {
      setMessage('Falha ao exportar CSV.');
    }
  };

  if (!token) {
    return (
      <div className="container">
        <h1>CivicReport Dashboard</h1>
        <div className="card">
          <label>
            Telefone
            <input value={phone} onChange={(e) => setPhone(e.target.value)} placeholder="+5511990000000" />
          </label>
          <button onClick={requestOtp}>Solicitar OTP</button>
          <label>
            OTP
            <input value={otp} onChange={(e) => setOtp(e.target.value)} placeholder="123456" />
          </label>
          <button onClick={verifyOtp}>Entrar</button>
          {message && <p className="message">{message}</p>}
        </div>
      </div>
    );
  }

  return (
    <div className="container">
      <header>
        <div>
          <h1>CivicReport Dashboard</h1>
          <p>{message}</p>
        </div>
        <button
          className="secondary"
          onClick={() => {
            localStorage.removeItem('token');
            setToken('');
          }}
        >
          Sair
        </button>
      </header>

      <section className="filters">
        <label>
          Categoria
          <select value={category} onChange={(e) => setCategory(e.target.value)}>
            <option value="">Todas</option>
            <option value="Dengue">DENGUE</option>
            <option value="Buraco">BURACO</option>
            <option value="MatoAlto">MATO_ALTO</option>
            <option value="Lixo">LIXO</option>
          </select>
        </label>
        <label>
          Status
          <select value={status} onChange={(e) => setStatus(e.target.value)}>
            <option value="">Todos</option>
            <option value="PendingModeration">PENDING_MODERATION</option>
            <option value="Approved">APPROVED</option>
            <option value="Rejected">REJECTED</option>
            <option value="NeedsReview">NEEDS_REVIEW</option>
          </select>
        </label>
        <label>
          De
          <input type="date" value={from} onChange={(e) => setFrom(e.target.value)} />
        </label>
        <label>
          Até
          <input type="date" value={to} onChange={(e) => setTo(e.target.value)} />
        </label>
        <button onClick={exportCsv}>Exportar CSV</button>
      </section>

      <section className="map-section">
        <MapContainer center={[-22.7, -47.6]} zoom={12} scrollWheelZoom={true} className="map">
          <TileLayer
            attribution='&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a>'
            url="https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png"
          />
          {reports.map((report) => (
            <Marker key={report.id} position={[report.lat, report.lng]} icon={markerIcon}>
              <Popup>
                <strong>{report.category}</strong>
                <p>{report.description}</p>
                <p>Status: {report.status}</p>
                <a href={report.publicPhotoUrl} target="_blank" rel="noreferrer">Ver foto</a>
              </Popup>
            </Marker>
          ))}
        </MapContainer>
      </section>

      <section className="table-section">
        <table>
          <thead>
            <tr>
              <th>ID</th>
              <th>Categoria</th>
              <th>Descrição</th>
              <th>Status</th>
              <th>Criado em</th>
              <th>Foto</th>
            </tr>
          </thead>
          <tbody>
            {reports.map((report) => (
              <tr key={report.id}>
                <td>{report.id.slice(0, 8)}</td>
                <td>{report.category}</td>
                <td>{report.description}</td>
                <td>{report.status}</td>
                <td>{new Date(report.createdAt).toLocaleString()}</td>
                <td>
                  <a href={report.publicPhotoUrl} target="_blank" rel="noreferrer">Abrir</a>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </section>
    </div>
  );
}
