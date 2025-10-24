import { useCallback, useEffect, useMemo, useState } from 'react'
import './App.css'

const API_BASE_URL = import.meta.env.VITE_API_BASE_URL ?? 'http://localhost:5000'

const messageRoleLabels = {
  user: 'You',
  bot: 'Assistant',
  system: 'System',
}

function App() {
  const [token, setToken] = useState(() => window.localStorage.getItem('token') ?? '')
  const [displayName, setDisplayName] = useState(
    () => window.localStorage.getItem('displayName') ?? '',
  )
  const [conversations, setConversations] = useState([])
  const [selectedConversationId, setSelectedConversationId] = useState(() =>
    window.localStorage.getItem('activeConversationId') ?? '',
  )
  const [messages, setMessages] = useState([])
  const [loginForm, setLoginForm] = useState({ username: '', password: '' })
  const [messageDraft, setMessageDraft] = useState('')
  const [isLoadingConversations, setIsLoadingConversations] = useState(false)
  const [isLoadingMessages, setIsLoadingMessages] = useState(false)
  const [isSendingMessage, setIsSendingMessage] = useState(false)
  const [error, setError] = useState('')

  const hasSession = Boolean(token)

  const timeFormatter = useMemo(
    () =>
      new Intl.DateTimeFormat(undefined, {
        hour: 'numeric',
        minute: 'numeric',
      }),
    [],
  )

  const dateFormatter = useMemo(
    () =>
      new Intl.DateTimeFormat(undefined, {
        dateStyle: 'medium',
        timeStyle: 'short',
      }),
    [],
  )

  const callApi = useCallback(
    async (path, { method = 'GET', body } = {}) => {
      const headers = new Headers({ 'Content-Type': 'application/json' })

      if (token) {
        headers.set('Authorization', `Bearer ${token}`)
      }

      const response = await fetch(`${API_BASE_URL}${path}`, {
        method,
        headers,
        body: body ? JSON.stringify(body) : undefined,
      })

      if (!response.ok) {
        let message = `${response.status} ${response.statusText}`
        try {
          const details = await response.json()
          if (typeof details?.error === 'string') {
            message = details.error
          }
        } catch {
          /* ignore */
        }
        throw new Error(message)
      }

      if (response.status === 204) {
        return null
      }

      return response.json()
    },
    [token],
  )

  const loadConversations = useCallback(async () => {
    if (!token) {
      return
    }
    setIsLoadingConversations(true)
    try {
      const data = await callApi('/conversations')
      setConversations(data)
      if (data.length === 0) {
        setSelectedConversationId('')
        setMessages([])
        window.localStorage.removeItem('activeConversationId')
        return
      }

      if (!selectedConversationId || !data.find((item) => item.id === selectedConversationId)) {
        const newest = data.at(0)
        if (newest) {
          setSelectedConversationId(newest.id)
          window.localStorage.setItem('activeConversationId', newest.id)
        }
      }
    } catch (apiError) {
      setError(apiError.message)
    } finally {
      setIsLoadingConversations(false)
    }
  }, [callApi, selectedConversationId, token])

  const loadConversationMessages = useCallback(
    async (conversationId) => {
      if (!token || !conversationId) {
        return
      }
      setIsLoadingMessages(true)
      try {
        const data = await callApi(`/conversations/${conversationId}`)
        setMessages(data.messages ?? [])
      } catch (apiError) {
        setError(apiError.message)
      } finally {
        setIsLoadingMessages(false)
      }
    },
    [callApi, token],
  )

  useEffect(() => {
    if (token) {
      loadConversations()
    } else {
      setConversations([])
      setMessages([])
      setSelectedConversationId('')
    }
  }, [loadConversations, token])

  useEffect(() => {
    if (selectedConversationId) {
      loadConversationMessages(selectedConversationId)
    }
  }, [loadConversationMessages, selectedConversationId])

  const handleLogin = async (event) => {
    event.preventDefault()
    setError('')

    const username = loginForm.username.trim()
    if (!username) {
      setError('Username is required.')
      return
    }

    try {
      const data = await callApi('/auth/login', {
        method: 'POST',
        body: {
          username: loginForm.username,
          password: loginForm.password,
        },
      })

      setToken(data.token)
      setDisplayName(data.displayName)
      window.localStorage.setItem('token', data.token)
      window.localStorage.setItem('displayName', data.displayName)
      setLoginForm({ username: '', password: '' })
    } catch (apiError) {
      setError(apiError.message)
    }
  }

  const handleLogout = () => {
    setToken('')
    setDisplayName('')
    setConversations([])
    setMessages([])
    setSelectedConversationId('')
    window.localStorage.removeItem('token')
    window.localStorage.removeItem('displayName')
    window.localStorage.removeItem('activeConversationId')
  }

  const handleSelectConversation = (conversationId) => {
    setSelectedConversationId(conversationId)
    window.localStorage.setItem('activeConversationId', conversationId)
  }

  const handleCreateConversation = async () => {
    if (!token) {
      setError('Please sign in to start chatting.')
      return
    }

    setError('')
    try {
      const data = await callApi('/conversations', { method: 'POST', body: {} })
      await loadConversations()
      setSelectedConversationId(data.id)
      window.localStorage.setItem('activeConversationId', data.id)
      setMessages(data.messages ?? [])
    } catch (apiError) {
      setError(apiError.message)
    }
  }

  const handleSendMessage = async (event) => {
    event.preventDefault()
    if (!messageDraft.trim() || !selectedConversationId) {
      return
    }

    setIsSendingMessage(true)
    setError('')
    try {
      const data = await callApi(`/conversations/${selectedConversationId}/messages`, {
        method: 'POST',
        body: { message: messageDraft.trim() },
      })
      setMessages(data.messages ?? [])
      setMessageDraft('')
      await loadConversations()
    } catch (apiError) {
      setError(apiError.message)
    } finally {
      setIsSendingMessage(false)
    }
  }

  if (!hasSession) {
    return (
      <div className="app app--centered">
        <div className="card login-card">
          <h1 className="title">Rasa Chat Dashboard</h1>
          <p className="subtitle">Sign in with any credentials to continue.</p>
          {error && <div className="error-banner">{error}</div>}
          <form className="form" onSubmit={handleLogin}>
            <label className="label">
              Username
              <input
                className="input"
                type="text"
                value={loginForm.username}
                onChange={(event) =>
                  setLoginForm((state) => ({ ...state, username: event.target.value }))
                }
                placeholder="e.g. alex"
              />
            </label>
            <label className="label">
              Password
              <input
                className="input"
                type="password"
                value={loginForm.password}
                onChange={(event) =>
                  setLoginForm((state) => ({ ...state, password: event.target.value }))
                }
                placeholder="Any value works"
              />
            </label>
            <button className="button button--primary" type="submit">
              Continue
            </button>
          </form>
        </div>
      </div>
    )
  }

  return (
    <div className="app">
      <aside className="sidebar">
        <div className="sidebar__header">
          <div>
            <p className="sidebar__greeting">Welcome back</p>
            <p className="sidebar__name">{displayName}</p>
          </div>
          <button className="button" onClick={handleLogout}>
            Sign out
          </button>
        </div>

        <button className="button button--primary sidebar__new" onClick={handleCreateConversation}>
          + New chat
        </button>

        <div className="sidebar__list">
          {isLoadingConversations ? (
            <p className="sidebar__hint">Loading conversations…</p>
          ) : conversations.length === 0 ? (
            <p className="sidebar__hint">Start your first chat to see it here.</p>
          ) : (
            conversations.map((conversation) => (
              <button
                key={conversation.id}
                className={`conversation ${conversation.id === selectedConversationId ? 'conversation--active' : ''}`}
                onClick={() => handleSelectConversation(conversation.id)}
              >
                <span className="conversation__title">{conversation.title}</span>
                <span className="conversation__time">
                  {dateFormatter.format(new Date(conversation.updatedAt))}
                </span>
              </button>
            ))
          )}
        </div>
      </aside>

      <main className="chat">
        <header className="chat__header">
          <div>
            <h2 className="chat__title">
              {selectedConversationId
                ? conversations.find((item) => item.id === selectedConversationId)?.title ??
                  'Conversation'
                : 'Start a conversation'}
            </h2>
            {error && <div className="error-banner">{error}</div>}
          </div>
          <button className="button" onClick={handleCreateConversation}>
            New chat
          </button>
        </header>

        {selectedConversationId ? (
          <div className="chat__content">
            <div className="messages" data-loading={isLoadingMessages}>
              {isLoadingMessages ? (
                <div className="messages__placeholder">Loading messages…</div>
              ) : messages.length === 0 ? (
                <div className="messages__placeholder">Send a message to begin.</div>
              ) : (
                messages.map((message, index) => (
                  <div
                    key={`${message.timestamp}-${index}`}
                    className={`message message--${message.role}`}
                  >
                    <div className="message__meta">
                      <span className="message__author">
                        {messageRoleLabels[message.role] ?? message.role}
                      </span>
                      <span className="message__time">
                        {timeFormatter.format(new Date(message.timestamp))}
                      </span>
                    </div>
                    <div className="message__bubble">{message.text}</div>
                  </div>
                ))
              )}
            </div>
            <form className="composer" onSubmit={handleSendMessage}>
              <textarea
                className="composer__input"
                value={messageDraft}
                placeholder="Type your message…"
                onChange={(event) => setMessageDraft(event.target.value)}
                rows={1}
              />
              <button className="button button--primary" type="submit" disabled={isSendingMessage}>
                {isSendingMessage ? 'Sending…' : 'Send'}
              </button>
            </form>
          </div>
        ) : (
          <div className="chat__empty">
            <h3>Choose a conversation</h3>
            <p>Select a conversation on the left or start a new chat.</p>
            <button className="button button--primary" onClick={handleCreateConversation}>
              Start chatting
            </button>
          </div>
        )}
      </main>
    </div>
  )
}

export default App
