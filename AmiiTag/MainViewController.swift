//
//  MainView.swift
//  AmiiTag
//
//  Created by Daniel Radtke on 6/28/20.
//  Copyright © 2020 Daniel Radtke. All rights reserved.
//

import Foundation
import UIKit
import CoreNFC
import MobileCoreServices
import SwiftyBluetooth

class MainViewController: UIViewController, NFCTagReaderSessionDelegate, UIDocumentPickerDelegate, ScannerViewControllerDelegate {
    @IBOutlet var logo: UIImageView!
    
    func scannerCodeFound(code: String) {
        guard let data = try? Data(base64Encoded: code, options: .ignoreUnknownCharacters) else {
            return
        }
        
        if (data.count == 532 || data.count == 540 || data.count == 572) {
            if let dump = TagDump(data: data) {
                dismiss(animated: true) {
                    MainViewController.openTagInfo(dump: dump, controller: self)
                }
            }
        }
    }
    
    var tagReaderSession: NFCTagReaderSession?
    var pickerController: UIDocumentPickerViewController!
    
    @IBAction func loadTagTap(_ sender: Any) {
        self.present(pickerController, animated: true)
    }
    
    @IBAction func startTagReadingSession() {
        if (PuckPeripheral.pucks.count > 0) {
            let alertController = UIAlertController(title: nil, message: nil, preferredStyle: .actionSheet)
            alertController.view.tintColor = self.view.tintColor
            
            for puck in PuckPeripheral.pucks.sorted(by: { (a, b) -> Bool in
                return a.name ?? "Puck" > b.name ?? "Puck"
            }) {
                alertController.addAction(UIAlertAction(title: puck.name, style: .default, handler: { (action) in
                    let alert = UIAlertController(title: "Please Wait", message: "Reading " + (puck.name ?? "Puck"), preferredStyle: .alert)
                    self.present(alert, animated: true)
                    
                    puck.readTag { (result) in
                        switch result {
                        case .success(let tag):
                            self.dismiss(animated: true)
                            MainViewController.openTagInfo(dump: TagDump(data: tag)!, controller: self)
                            break
                        case .failure(let error):
                            self.dismiss(animated: true)
                            let errorAlert = UIAlertController(title: "Oh no!", message: error.localizedDescription, preferredStyle: .alert)
                            errorAlert.addAction(UIAlertAction(title: "OK", style: .default, handler: nil))
                            self.present(errorAlert, animated: true)
                            break
                        }
                        
                        puck.disconnect { (result) in
                            PuckPeripheral.startScanning()
                        }
                    }
                    
                }))
            }
            
            alertController.addAction(UIAlertAction(title: "NFC", style: .default){ action -> Void in
                self.startNfcTagReadingSession()
            })
            
            alertController.addAction(UIAlertAction(title: "Cancel", style: .cancel){ action -> Void in })
            self.present(alertController, animated:true){}
        } else {
            self.startNfcTagReadingSession()
        }
    }
    
    func startNfcTagReadingSession() {
        tagReaderSession = NFCTagReaderSession(pollingOption: NFCTagReaderSession.PollingOption.iso14443, delegate: self)
        tagReaderSession?.alertMessage = "Hold tag to back of phone!"
        tagReaderSession?.begin()
    }
    
    @IBAction func scanQrTap(_ sender: Any) {
        let storyboard = UIStoryboard(name: "Main", bundle: nil)
        guard let vc = storyboard.instantiateViewController(withIdentifier: "ScannerView") as? ScannerViewController else {
            return
        }
        vc.delegate = self
        self.present(vc, animated: true)
    }
    
    @IBAction func amiiboLibraryTap(_ sender: Any) {
        let storyboard = UIStoryboard(name: "Main", bundle: nil)
        guard let view = storyboard.instantiateViewController(withIdentifier: "AmiiboSeries") as? UIViewController else {
            return
        }
        view.title = "Amiibo Library"
        self.present(UINavigationController(rootViewController: view), animated: true)
    }
    
    static func openTagInfo(dump: TagDump, controller: UIViewController){
        let storyboard = UIStoryboard(name: "Main", bundle: nil)
        guard let view = storyboard.instantiateViewController(withIdentifier: "TagInfo") as? TagInfoViewController else {
            return
        }
        let nc = UINavigationController(rootViewController: view)
        controller.present(nc, animated: true)
        view.amiiboData = dump
    }
    
    func handleConnectedTag(tag: NFCMiFareTag) {
        NTAG215Tag.initialize(tag: tag) { result in
            switch result {
            case .success(let ntag215Tag):
                self.tagReaderSession?.invalidate()
                DispatchQueue.main.async {
                    print(ntag215Tag.dump.TagUIDSig)
                    self.writeUidSig(data: ntag215Tag.dump.TagUIDSig!)
                    MainViewController.openTagInfo(dump: ntag215Tag.dump, controller: self)
                }
            case .failure(let error):
                self.tagReaderSession?.invalidate(errorMessage: error.localizedDescription)
            }
        }
    }
    
    func writeUidSig(data: Data) {
        let fileManager = FileManager.default
        let documentsURL =  fileManager.urls(for: .documentDirectory, in: .userDomainMask).first!

        let sigPath = documentsURL.appendingPathComponent("signatures")
        do
        {
            try FileManager.default.createDirectory(atPath: sigPath.path, withIntermediateDirectories: true, attributes: nil)
            try data.write(to: sigPath.appendingPathComponent("\(data[0..<9].map { String(format: "%02hhx", $0) }.joined()).bin"))
        }
        catch let error as NSError
        {
            NSLog("Unable to create directory \(error.debugDescription)")
        }
    }
    
    override func viewDidLoad() {
        super.viewDidLoad()
        
        print("Loaded \(NTAG215Tag.uidSignatures.count) UID/Signature pairs")
        print("Loaded \(AmiiboDatabase.amiiboDumps.count) amiibo dumps")
        
        pickerController = UIDocumentPickerViewController(documentTypes: [kUTTypeData as String], in: .open)
        pickerController.delegate = self
        pickerController.allowsMultipleSelection = false
        
        let tapGestureRecognizer = UITapGestureRecognizer(target: self, action: #selector(imageTapped(tapGestureRecognizer:)))
        logo.isUserInteractionEnabled = true
        logo.addGestureRecognizer(tapGestureRecognizer)
    }
    
    @objc func imageTapped(tapGestureRecognizer: UITapGestureRecognizer)
    {
        if PuckPeripheral.pucks.count > 0 {
            let alertController = UIAlertController(title: "Puck Settings", message: nil, preferredStyle: .actionSheet)
            alertController.view.tintColor = self.view.tintColor
            
            for puck in PuckPeripheral.pucks.sorted(by: { (a, b) -> Bool in
                return a.name ?? "Puck" > b.name ?? "Puck"
            }) {
                alertController.addAction(UIAlertAction(title: puck.name, style: .default, handler: { (action) in
                    let alertController = UIAlertController(title: puck.name ?? "Puck", message: nil, preferredStyle: .actionSheet)
                    
                    alertController.addAction(UIAlertAction(title: "Change Name", style: .default, handler: { (action) in
                        let alert = UIAlertController(title: puck.name ?? "Puck", message: nil, preferredStyle: .alert)
                        alert.addAction(UIAlertAction(title: "Cancel", style: .cancel, handler: nil))

                        alert.addTextField(configurationHandler: { textField in
                            textField.placeholder = "Enter new puck name"
                        })

                        alert.addAction(UIAlertAction(title: "OK", style: .default, handler: { action in
                            puck.changeName(name: alert.textFields?.first?.text ?? "") { (result) in
                                switch result {
                                case .success(()):
                                    break
                                case .failure(let error):
                                    self.dismiss(animated: true)
                                    let errorAlert = UIAlertController(title: "Oh no!", message: error.localizedDescription, preferredStyle: .alert)
                                    errorAlert.addAction(UIAlertAction(title: "OK", style: .default, handler: nil))
                                    self.present(errorAlert, animated: true)
                                    break
                                }
                            }
                        }))

                        self.present(alert, animated: true)
                    }))
                    
                    alertController.addAction(UIAlertAction(title: "Enable Uart", style: .default, handler: { (action) in
                        puck.enableUart(completionHandler: { (result) in
                            switch result {
                            case .success(()):
                                puck.disconnect { (result) in }
                                break
                            case .failure(let error):
                                self.dismiss(animated: true)
                                let errorAlert = UIAlertController(title: "Oh no!", message: error.localizedDescription, preferredStyle: .alert)
                                errorAlert.addAction(UIAlertAction(title: "OK", style: .default, handler: nil))
                                self.present(errorAlert, animated: true)
                                break
                            }
                        })
                    }))
                    
                    alertController.addAction(UIAlertAction(title: "Cancel", style: .cancel){ action -> Void in
                        self.imageTapped(tapGestureRecognizer: tapGestureRecognizer)
                    })
                    
                    self.present(alertController, animated: true, completion: nil)
                }))
            }
            
            alertController.addAction(UIAlertAction(title: "Cancel", style: .cancel){ action -> Void in })
            self.present(alertController, animated:true){}
        }
    }
    
    // MARK: DocumentDelegate
    public func documentPicker(_ controller: UIDocumentPickerViewController, didPickDocumentsAt urls: [URL]) {
        guard let url = urls.first else {
            return
        }
        if url.isFileURL {
            let shouldStopAccessing = url.startAccessingSecurityScopedResource()
            let path = url.path
            if let attr = try? FileManager.default.attributesOfItem(atPath: path) as NSDictionary {
                if attr.fileSize() == 532 || attr.fileSize() == 540 || attr.fileSize() == 572 {
                    if let data = try? Data(contentsOf: url),
                        let dump = TagDump(data: data) {
                        MainViewController.openTagInfo(dump: dump, controller: self)
                    }
                    
                }
            }
            if shouldStopAccessing {
                url.stopAccessingSecurityScopedResource()
            }
        }
    }
    
    // MARK: NFCTagReaderSessionDelegate
    func tagReaderSessionDidBecomeActive(_ session: NFCTagReaderSession) {
        NSLog("tagReaderSessionDidBecomeActive")
    }
    
    func tagReaderSession(_ session: NFCTagReaderSession, didInvalidateWithError error: Error) {
        NSLog("NFCTagReaderSession, didInvalidateWithError \(error)")
    }
    
    func tagReaderSession(_ session: NFCTagReaderSession, didDetect tags: [NFCTag]) {
        guard case let NFCTag.miFare(tag) = tags.first! else {
            tagReaderSession?.invalidate(errorMessage: "Invalid tag type.")
            return
        }
        session.connect(to: tags.first!) { (error: Error?) in
            if let error = error {
                session.invalidate(errorMessage: error.localizedDescription)
            } else {
                self.handleConnectedTag(tag: tag)
            }
        }
    }
}
