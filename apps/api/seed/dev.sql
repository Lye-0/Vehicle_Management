INSERT OR IGNORE INTO customers (id, customer_number, name, name_kana, postal_code, address, phone, email, memo)
VALUES ('customer-sato', 'C-DEMO001', '佐藤 太郎', 'さとう たろう', '100-0001', '東京都千代田区千代田1-1', '090-1234-5678', 'sato.taro@example.com', '土曜午前の来店が多い。メールより電話を希望。');
INSERT OR IGNORE INTO customers (id, customer_number, name, name_kana, postal_code, address, phone, email, memo)
VALUES ('customer-tanaka', 'C-DEMO002', '田中 花子', 'たなか はなこ', '231-0001', '神奈川県横浜市中区', '080-2345-6789', 'tanaka.hanako@example.com', NULL);
INSERT OR IGNORE INTO customers (id, customer_number, name, name_kana, postal_code, address, phone, email, memo)
VALUES ('customer-suzuki', 'C-DEMO003', '鈴木 一郎', 'すずき いちろう', '330-0001', '埼玉県さいたま市大宮区', '070-3456-7890', 'suzuki.ichiro@example.com', NULL);
INSERT OR IGNORE INTO customers (id, customer_number, name, name_kana, postal_code, address, phone, email, memo)
VALUES ('customer-yamada', 'C-DEMO004', '山田 恵子', 'やまだ けいこ', '210-0001', '神奈川県川崎市川崎区', '090-4567-8901', 'yamada.keiko@example.com', NULL);

INSERT OR IGNORE INTO vehicles (id, customer_id, maker, name, registration_number, chassis_number, model_year, inspection_date, mileage, body_color, displacement, transmission, memo)
VALUES ('vehicle-sato-prius', 'customer-sato', 'トヨタ', 'プリウス', '品川 500 あ 1234', 'ZVW5000001', 2020, '2026/10/15', 68420, 'パールホワイト', 1800, 'CVT', '左後ドア小傷あり。次回点検時に要確認。');
INSERT OR IGNORE INTO vehicles (id, customer_id, maker, name, registration_number, chassis_number, model_year, inspection_date, mileage, body_color, displacement, transmission, memo)
VALUES ('vehicle-sato-hilux', 'customer-sato', 'トヨタ', 'ハイラックス', '品川 300 か 5678', 'GUN1250002', 2022, '2027/04/08', 31280, 'アティチュードブラック', 2400, '6AT', '休日利用。');
INSERT OR IGNORE INTO vehicles (id, customer_id, maker, name, registration_number, chassis_number, model_year, inspection_date, mileage, body_color, displacement, transmission, memo)
VALUES ('vehicle-tanaka-fit', 'customer-tanaka', 'ホンダ', 'フィット', '横浜 300 い 5678', 'GK3000003', 2019, '2026/08/20', 42100, 'ミッドナイトブルー', 1300, 'CVT', NULL);
INSERT OR IGNORE INTO vehicles (id, customer_id, maker, name, registration_number, chassis_number, model_year, inspection_date, mileage, body_color, displacement, transmission, memo)
VALUES ('vehicle-suzuki-note', 'customer-suzuki', 'ニッサン', 'ノート', '大宮 400 う 9012', 'E1200004', 2018, '2025/12/01', 93750, 'ブリリアントシルバー', 1200, 'CVT', '車検期限を超過。早急に案内。');
INSERT OR IGNORE INTO vehicles (id, customer_id, maker, name, registration_number, chassis_number, model_year, inspection_date, mileage, body_color, displacement, transmission, memo)
VALUES ('vehicle-yamada-cx5', 'customer-yamada', 'マツダ', 'CX-5', '川崎 501 お 7890', 'KF2000005', 2021, '2027/03/31', 31200, 'ソウルレッド', 2000, '6AT', NULL);
