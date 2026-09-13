<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { ElAlert, ElButton, ElConfigProvider, ElIcon, ElInput, ElSkeleton, ElTag } from 'element-plus'
import zhCn from 'element-plus/es/locale/lang/zh-cn'
import { Cpu } from '@element-plus/icons-vue'
import { ApiError, currentUser, login, logout, requestError, roleLabels } from './api'
import type { CurrentUser } from './api'
import TracePage from './TracePage.vue'

const user = ref<CurrentUser | null>(null)
const checkingSession = ref(true)
const submitting = ref(false)
const signingOut = ref(false)
const userName = ref('')
const password = ref('')
const authError = ref('')
const logoutError = ref('')
const connectionFailed = ref(false)

async function acceptUser(value: CurrentUser) {
  if (!value.roles.some(role => role in roleLabels)) {
    await logout()
    throw new Error('该账号不能进入质量平台，请使用人员账号登录。')
  }
  user.value = value
}

async function checkSession() {
  checkingSession.value = true
  authError.value = ''
  connectionFailed.value = false
  try { await acceptUser(await currentUser()) }
  catch (cause) {
    if (!(cause instanceof ApiError && cause.status === 401)) {
      authError.value = requestError(cause)
      connectionFailed.value = !(cause instanceof ApiError)
    }
  } finally { checkingSession.value = false }
}

async function signIn() {
  if (submitting.value) return
  submitting.value = true
  authError.value = ''
  connectionFailed.value = false
  try { await acceptUser(await login(userName.value.trim(), password.value)) }
  catch (cause) {
    authError.value = cause instanceof ApiError && cause.status === 401 ? '账号或密码不正确，请重新输入。' : requestError(cause)
  } finally {
    password.value = ''
    submitting.value = false
  }
}

async function signOut() {
  signingOut.value = true
  logoutError.value = ''
  try {
    await logout()
    user.value = null
    authError.value = ''
  } catch (cause) {
    if (cause instanceof ApiError && cause.status === 401) sessionExpired()
    else logoutError.value = `退出未完成：${requestError(cause)}`
  } finally { signingOut.value = false }
}

function sessionExpired() {
  user.value = null
  password.value = ''
  logoutError.value = ''
  authError.value = '登录已失效，请重新登录。'
}

onMounted(checkSession)
</script>

<template>
  <ElConfigProvider :locale="zhCn">
    <div class="app-shell">
      <header class="topbar">
        <a class="brand" href="/" aria-label="BoardTrace 检测追溯首页">
          <span class="brand-mark"><ElIcon :size="27"><Cpu /></ElIcon></span>
          <span><strong>BoardTrace</strong><small>PCB 视觉检测与质量追溯</small></span>
        </a>
        <div class="account-bar">
          <div class="workspace-label"><span class="mode-dot"></span>工业业务模拟</div>
          <template v-if="user">
            <span class="account-name">{{ user.displayName }}</span>
            <ElTag v-for="role in user.roles.filter(role => role in roleLabels)" :key="role" type="info">{{ roleLabels[role] }}</ElTag>
            <ElButton :loading="signingOut" @click="signOut">退出登录</ElButton>
          </template>
        </div>
      </header>
      <main v-if="checkingSession" class="login-panel" aria-busy="true" aria-label="正在验证登录"><ElSkeleton :rows="4" animated /></main>
      <main v-else-if="!user" class="login-panel">
        <p class="eyebrow">中央质量平台</p>
        <h1>登录 BoardTrace</h1>
        <p class="subtitle">使用操作员、工艺或质量账号查看检测档案。</p>
        <ElAlert v-if="authError" :title="authError" type="error" show-icon :closable="false" role="alert" />
        <ElButton v-if="connectionFailed" @click="checkSession">重新连接</ElButton>
        <form class="login-form" @submit.prevent="signIn">
          <div class="filter-field"><label for="login-user">账号</label><ElInput id="login-user" v-model="userName" autocomplete="username" required maxlength="128" :disabled="submitting" placeholder="输入人员账号" /></div>
          <div class="filter-field"><label for="login-password">密码</label><ElInput id="login-password" v-model="password" type="password" autocomplete="current-password" required :disabled="submitting" placeholder="输入密码" /></div>
          <ElButton type="primary" native-type="submit" :loading="submitting">登录</ElButton>
        </form>
      </main>
      <template v-else>
        <ElAlert v-if="logoutError" :title="logoutError" type="error" show-icon :closable="false" role="alert" />
        <TracePage :key="user.id" @session-expired="sessionExpired" />
      </template>
    </div>
  </ElConfigProvider>
</template>
