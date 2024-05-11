import http from 'k6/http';
import { sleep } from 'k6';

export default function () {
  http.get('https://crazyapi20240507084000.azurewebsites.net/status/');
  sleep(1);
}
